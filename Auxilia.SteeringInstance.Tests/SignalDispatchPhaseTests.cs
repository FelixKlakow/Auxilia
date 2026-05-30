using System.Security.Cryptography;
using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Tests;

[TestFixture]
public class SignalDispatchPhaseTests
{
    #region Store tests

    [Test]
    public void SignalHandlerStore_UpsertAndGet_ReturnsStoredHandlers()
    {
        var store = new SignalHandlerStore();
        store.UpsertHandler("TestWorkflow", new StoredSignalHandlerConfiguration("signal-a", new InvokeWorkflowSignalHandler("wf-target")));
        store.UpsertHandler("TestWorkflow", new StoredSignalHandlerConfiguration("signal-b", new NullSignalHandler()));

        var handlers = store.GetHandlers("TestWorkflow");

        Assert.That(handlers, Has.Count.EqualTo(2));
        Assert.That(handlers.Any(h => h.SignalName == "signal-a" && h.HandlerDescriptor is InvokeWorkflowSignalHandler), Is.True);
        Assert.That(handlers.Any(h => h.SignalName == "signal-b" && h.HandlerDescriptor is NullSignalHandler), Is.True);
    }

    [Test]
    public void SignalHandlerStore_MarkDirty_DoesNotRemoveHandlers()
    {
        var store = new SignalHandlerStore();
        store.UpsertHandler("TestWorkflow", new StoredSignalHandlerConfiguration("signal-a", new NullSignalHandler()));

        store.MarkDirty("TestWorkflow");

        var handlers = store.GetHandlers("TestWorkflow");
        Assert.That(handlers, Has.Count.EqualTo(1));
        Assert.That(handlers[0].SignalName, Is.EqualTo("signal-a"));
    }

    #endregion

    #region ConfigurationResolver tests

    [Test]
    public void ConfigurationResolver_Resolve_IncludesSignalHandlers_WhenPresent()
    {
        var slotStore = new SlotConfigurationStore();
        slotStore.UpsertConfiguration("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Valid));

        var signalStore = new SignalHandlerStore();
        signalStore.UpsertHandler("TestWorkflow",
            new StoredSignalHandlerConfiguration("signal-a", new InvokeWorkflowSignalHandler("target-wf")));

        var resolver = new ConfigurationResolver(slotStore, signalStore, NullLogger<ConfigurationResolver>.Instance);

        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var result = resolver.Resolve("TestWorkflow", publicKey);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.SignalHandlers, Contains.Key("signal-a"));
        Assert.That(result.SignalHandlers["signal-a"], Is.InstanceOf<InvokeWorkflowSignalHandler>());
    }

    [Test]
    public void ConfigurationResolver_Resolve_ReturnsEmptySignalHandlers_WhenNoneConfigured()
    {
        var slotStore = new SlotConfigurationStore();
        slotStore.UpsertConfiguration("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Valid));

        var signalStore = new SignalHandlerStore(); // empty — no handlers

        var resolver = new ConfigurationResolver(slotStore, signalStore, NullLogger<ConfigurationResolver>.Instance);

        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var result = resolver.Resolve("TestWorkflow", publicKey);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.SignalHandlers, Is.Empty);
    }

    #endregion

    #region WorkflowRegistrationHandler tests

    [Test]
    public async Task WorkflowRegistrationHandler_OnSuccess_PublishesConfigurationResponse_WithSignalHandlers()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var slotStore = new SlotConfigurationStore();
        slotStore.UpsertConfiguration("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Valid));

        var signalStore = new SignalHandlerStore();
        signalStore.UpsertHandler("TestWorkflow",
            new StoredSignalHandlerConfiguration("on-done", new NotifySignalHandler("email", new Dictionary<string, string>())));

        var resolver = new ConfigurationResolver(slotStore, signalStore, NullLogger<ConfigurationResolver>.Instance);
        var registry = new WorkflowInstanceRegistry();

        var bus = new CapturingBus();
        var profile = new RunnerProfile { AvailableTools = new HashSet<string>(), OpenPorts = new HashSet<int>() };
        var validator = new EnvironmentValidator(Options.Create(profile), NullLogger<EnvironmentValidator>.Instance);
        var handler = new WorkflowRegistrationHandler(bus, validator, resolver, registry, NullLogger<WorkflowRegistrationHandler>.Instance);

        await handler.StartAsync(CancellationToken.None);

        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(),
            new WorkflowManifest("TestWorkflow", Guid.NewGuid().ToString(), [], [], string.Empty, [], []),
            publicKey,
            "reply-topic");

        await bus.InvokeRegistrationAsync(request, CancellationToken.None);

        Assert.That(bus.Published, Has.Count.EqualTo(1));
        var response = (WorkflowConfigurationResponse)bus.Published[0].Message;
        Assert.That(response.Success, Is.True);
        Assert.That(response.SignalHandlers, Contains.Key("on-done"));
        Assert.That(response.SignalHandlers["on-done"], Is.InstanceOf<NotifySignalHandler>());
    }

    #endregion

    #region WorkflowInstanceRegistry tests

    [Test]
    public void WorkflowInstanceRegistry_Register_TryGetWorkflowType_ReturnsCorrectName()
    {
        var registry = new WorkflowInstanceRegistry();
        var instanceId = Guid.NewGuid();

        registry.Register(instanceId, "MyWorkflow");

        var found = registry.TryGetWorkflowType(instanceId, out var typeName);

        Assert.That(found, Is.True);
        Assert.That(typeName, Is.EqualTo("MyWorkflow"));
    }

    #endregion

    #region SignalDispatcher tests

    [Test]
    public async Task SignalDispatcher_InvokeWorkflowSignalHandler_PublishesToSignalRoutesQueue()
    {
        var bus = new CapturingBus();
        var handlerStore = new SignalHandlerStore();
        handlerStore.UpsertHandler("TestWorkflow",
            new StoredSignalHandlerConfiguration("fire", new InvokeWorkflowSignalHandler("target-wf")));

        var registry = new WorkflowInstanceRegistry();
        var instanceId = Guid.NewGuid();
        registry.Register(instanceId, "TestWorkflow");

        var dispatcher = new SignalDispatcher(bus, handlerStore, registry, NullLogger<SignalDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        await bus.InvokeSignalAsync(new WorkflowSignalMessage(instanceId, "fire", "{}"), CancellationToken.None);

        var routeMsg = bus.Published.SingleOrDefault(p => p.Topic == "workflow.signal-routes");
        Assert.That(routeMsg.Message, Is.Not.Null);
        Assert.That(routeMsg.Message, Is.InstanceOf<WorkflowSignalRouteMessage>());
        var route = (WorkflowSignalRouteMessage)routeMsg.Message;
        Assert.That(route.TargetWorkflowName, Is.EqualTo("target-wf"));
        Assert.That(route.OriginSignalName, Is.EqualTo("fire"));
    }

    [Test]
    public async Task SignalDispatcher_NullSignalHandler_PublishesNothing()
    {
        var bus = new CapturingBus();
        var handlerStore = new SignalHandlerStore();
        handlerStore.UpsertHandler("TestWorkflow",
            new StoredSignalHandlerConfiguration("silent", new NullSignalHandler()));

        var registry = new WorkflowInstanceRegistry();
        var instanceId = Guid.NewGuid();
        registry.Register(instanceId, "TestWorkflow");

        var dispatcher = new SignalDispatcher(bus, handlerStore, registry, NullLogger<SignalDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        await bus.InvokeSignalAsync(new WorkflowSignalMessage(instanceId, "silent", "{}"), CancellationToken.None);

        Assert.That(bus.Published, Is.Empty);
    }

    [Test]
    public async Task SignalDispatcher_UnknownSignalName_LogsWarning_DoesNotThrow()
    {
        var bus = new CapturingBus();
        var handlerStore = new SignalHandlerStore();
        handlerStore.UpsertHandler("TestWorkflow",
            new StoredSignalHandlerConfiguration("known", new NullSignalHandler()));

        var registry = new WorkflowInstanceRegistry();
        var instanceId = Guid.NewGuid();
        registry.Register(instanceId, "TestWorkflow");

        var dispatcher = new SignalDispatcher(bus, handlerStore, registry, NullLogger<SignalDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        Assert.DoesNotThrowAsync(async () =>
            await bus.InvokeSignalAsync(new WorkflowSignalMessage(instanceId, "unknown-signal", "{}"), CancellationToken.None));

        Assert.That(bus.Published, Is.Empty);
    }

    [Test]
    public async Task SignalDispatcher_NotifySignalHandler_PublishesToNotificationsQueue()
    {
        var bus = new CapturingBus();
        var handlerStore = new SignalHandlerStore();
        handlerStore.UpsertHandler("TestWorkflow",
            new StoredSignalHandlerConfiguration("notify-me", new NotifySignalHandler("email", new Dictionary<string, string>())));

        var registry = new WorkflowInstanceRegistry();
        var instanceId = Guid.NewGuid();
        registry.Register(instanceId, "TestWorkflow");

        var dispatcher = new SignalDispatcher(bus, handlerStore, registry, NullLogger<SignalDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        await bus.InvokeSignalAsync(new WorkflowSignalMessage(instanceId, "notify-me", "{\"data\":1}"), CancellationToken.None);

        var notifMsg = bus.Published.SingleOrDefault(p => p.Topic == "workflow.notifications");
        Assert.That(notifMsg.Message, Is.Not.Null);
        Assert.That(notifMsg.Message, Is.InstanceOf<WorkflowNotificationMessage>());
        var notif = (WorkflowNotificationMessage)notifMsg.Message;
        Assert.That(notif.WorkflowInstanceId, Is.EqualTo(instanceId));
        Assert.That(notif.SignalName, Is.EqualTo("notify-me"));
    }

    #endregion

    #region Fakes

    private sealed class CapturingBus : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];

        private Func<WorkflowRegistrationRequest, CancellationToken, Task>? _registrationHandler;
        private Func<WorkflowSignalMessage, CancellationToken, Task>? _signalHandler;

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, message!));
            return Task.CompletedTask;
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
        {
            Published.Add((exchangeName, message!));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler is Func<WorkflowRegistrationRequest, CancellationToken, Task> regHandler)
                _registrationHandler = regHandler;
            else if (handler is Func<WorkflowSignalMessage, CancellationToken, Task> sigHandler)
                _signalHandler = sigHandler;
            return Task.FromResult<IAsyncDisposable>(NoopDisposable.Instance);
        }

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
            string exchangeName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(NoopDisposable.Instance);

        public Task InvokeRegistrationAsync(WorkflowRegistrationRequest request, CancellationToken ct)
            => _registrationHandler!(request, ct);

        public Task InvokeSignalAsync(WorkflowSignalMessage message, CancellationToken ct)
            => _signalHandler!(message, ct);

        private sealed class NoopDisposable : IAsyncDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    #endregion
}

using System.Security.Cryptography;
using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Environment;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowRegistrationHandlerTests
{
    private static WorkflowRegistrationHandler MakeHandler(
        CapturingFakeMessageBusClient messageBus,
        EnvironmentValidator? envValidator = null,
        ConfigurationResolver? configResolver = null,
        WorkflowInstanceRegistry? instanceRegistry = null)
    {
        var profile = new RunnerProfile
        {
            AvailableTools = new HashSet<string>(),
            OpenPorts = new HashSet<int>()
        };
        var validator = envValidator ?? new EnvironmentValidator(
            Options.Create(profile),
            NullLogger<EnvironmentValidator>.Instance);

        var store = new SlotConfigurationStore();
        var signalStore = new SignalHandlerStore();
        var resolver = configResolver ?? new ConfigurationResolver(
            store,
            signalStore,
            NullLogger<ConfigurationResolver>.Instance);

        return new WorkflowRegistrationHandler(
            messageBus,
            validator,
            resolver,
            instanceRegistry ?? new WorkflowInstanceRegistry(),
            NullLogger<WorkflowRegistrationHandler>.Instance);
    }

    private static string ValidPublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    [Test]
    public async Task HandleAsync_EnvValidationFails_PublishesFailureResponse()
    {
        // Manifest requires git, but empty profile doesn't have it
        var manifest = new WorkflowManifest(
            "TestWorkflow", Guid.NewGuid().ToString(), [],
            [new ToolRequirement("git")],
            string.Empty, [], []);
        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(), manifest, ValidPublicKey(), "reply-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(bus.Published, Has.Count.EqualTo(1));
        var (topic, msg) = bus.Published[0];
        Assert.That(topic, Is.EqualTo(request.ResponseTopic));
        var response = (WorkflowConfigurationResponse)msg;
        Assert.That(response.Success, Is.False);
        Assert.That(response.ErrorMessage, Does.Contain("git"));
    }

    [Test]
    public async Task HandleAsync_ConfigNotFound_PublishesFailureResponse()
    {
        // Manifest declares a slot so the config resolver is reached — but the store is empty.
        var manifest = new WorkflowManifest(
            "TestWorkflow", Guid.NewGuid().ToString(),
            [new SlotDefinition("slotA", null) { ServiceType = typeof(object) }], // non-empty slots → resolver is consulted
            [], string.Empty, [], []);
        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(), manifest, ValidPublicKey(), "reply-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus); // empty store → no config found
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(bus.Published, Has.Count.EqualTo(1));
        var response = (WorkflowConfigurationResponse)bus.Published[0].Message;
        Assert.That(response.Success, Is.False);
        Assert.That(response.ErrorMessage, Does.Contain("No slot configurations"));
    }

    [Test]
    public async Task HandleAsync_DirtyConfig_PublishesFailureResponse()
    {
        var store = new SlotConfigurationStore();
        store.UpsertConfiguration("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Dirty));
        var resolver = new ConfigurationResolver(store, new SignalHandlerStore(), NullLogger<ConfigurationResolver>.Instance);

        // Manifest must declare the slot so the resolver is reached.
        var manifest = new WorkflowManifest(
            "TestWorkflow", Guid.NewGuid().ToString(),
            [new SlotDefinition("slotA", null) { ServiceType = typeof(object) }],
            [], string.Empty, [], []);
        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(), manifest, ValidPublicKey(), "reply-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, configResolver: resolver);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(bus.Published, Has.Count.EqualTo(1));
        var response = (WorkflowConfigurationResponse)bus.Published[0].Message;
        Assert.That(response.Success, Is.False);
        Assert.That(response.ErrorMessage, Does.Contain("dirty"));
    }

    [Test]
    public async Task HandleAsync_HappyPath_PublishesSuccessResponseToCorrectTopic()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var store = new SlotConfigurationStore();
        store.UpsertConfiguration("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Valid));
        var resolver = new ConfigurationResolver(store, new SignalHandlerStore(), NullLogger<ConfigurationResolver>.Instance);

        // Manifest must declare the slot so the config is resolved.
        var manifest = new WorkflowManifest(
            "TestWorkflow", Guid.NewGuid().ToString(),
            [new SlotDefinition("slotA", null) { ServiceType = typeof(object) }],
            [], string.Empty, [], []);
        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(), manifest, publicKey, "my-response-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, configResolver: resolver);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(bus.Published, Has.Count.EqualTo(1));
        var (topic, msg) = bus.Published[0];
        Assert.That(topic, Is.EqualTo("my-response-topic"));
        var response = (WorkflowConfigurationResponse)msg;
        Assert.That(response.Success, Is.True);
        Assert.That(response.Slots, Has.Count.EqualTo(1));
        Assert.That(response.Slots.ContainsKey("slotA"), Is.True);
    }

    [Test]
    public async Task HandleAsync_NoSlots_PublishesSuccessWithEmptySlots()
    {
        // A workflow with no declared slots must succeed immediately without consulting the store.
        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(),
            new WorkflowManifest("SlotlessWorkflow", Guid.NewGuid().ToString(),
                [], // no slots
                [], string.Empty, [], []),
            ValidPublicKey(),
            "reply-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus); // empty store — must NOT be consulted
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(bus.Published, Has.Count.EqualTo(1));
        var response = (WorkflowConfigurationResponse)bus.Published[0].Message;
        Assert.That(response.Success, Is.True);
        Assert.That(response.Slots, Is.Empty);
        Assert.That(response.ErrorMessage, Is.Null);
    }

    [Test]
    public async Task HandleAsync_NoSlots_TracksInstanceId()
    {
        var instanceId = Guid.NewGuid();
        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("SlotlessWorkflow", instanceId.ToString(),
                [], [], string.Empty, [], []),
            ValidPublicKey(), "reply");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(handler.RegisteredCount, Is.EqualTo(1));
    }

    [Test]
    public async Task HandleAsync_HappyPath_TracksInstanceId()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var store = new SlotConfigurationStore();
        store.UpsertConfiguration("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Valid));
        var resolver = new ConfigurationResolver(store, new SignalHandlerStore(), NullLogger<ConfigurationResolver>.Instance);

        var instanceId = Guid.NewGuid();
        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("TestWorkflow", instanceId.ToString(),
                [new SlotDefinition("slotA", null) { ServiceType = typeof(object) }], // must have a slot so resolver is reached
                [], string.Empty, [], []),
            publicKey,
            "reply");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, configResolver: resolver);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(handler.RegisteredCount, Is.EqualTo(1));
    }

    // A fake that captures the subscription callback so tests can invoke it directly.
    private sealed class CapturingFakeMessageBusClient : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];
        private Func<WorkflowRegistrationRequest, CancellationToken, Task>? _handler;

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
            if (handler is Func<WorkflowRegistrationRequest, CancellationToken, Task> typedHandler)
                _handler = typedHandler;
            return Task.FromResult<IAsyncDisposable>(new NullDisposable());
        }

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
            string exchangeName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(new NullDisposable());

        public Task InvokeAsync(WorkflowRegistrationRequest request, CancellationToken cancellationToken)
            => _handler!(request, cancellationToken);

        private sealed class NullDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

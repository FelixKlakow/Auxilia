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
        WorkflowInstanceRegistry? instanceRegistry = null,
        WorkflowInstanceTokenRegistry? tokenRegistry = null,
        bool requireInstanceToken = false,
        List<string>? approvedLongLivingWorkflowTypes = null)
    {
        var profile = new RunnerProfile
        {
            AvailableTools = new HashSet<string>(),
            OpenPorts = new HashSet<int>()
        };
        var validator = envValidator ?? new EnvironmentValidator(
            Options.Create(profile),
            NullLogger<EnvironmentValidator>.Instance);

        var store = TestStores.NewSlotConfigurationStore();
        var signalStore = TestStores.NewSignalHandlerStore();
        var resolver = configResolver ?? new ConfigurationResolver(
            store,
            signalStore,
            NullLogger<ConfigurationResolver>.Instance);

        var settings = Options.Create(new WorkflowDispatcherSettings
        {
            RequireInstanceToken = requireInstanceToken,
            ApprovedLongLivingWorkflowTypes = approvedLongLivingWorkflowTypes ?? []
        });

        return new WorkflowRegistrationHandler(
            messageBus,
            validator,
            resolver,
            instanceRegistry ?? TestStores.NewWorkflowInstanceRegistry(),
            tokenRegistry ?? new WorkflowInstanceTokenRegistry(settings, TimeProvider.System),
            TestStores.NewAuditLog(),
            TestStores.NewStatusPublisher(messageBus),
            settings,
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
        var store = TestStores.NewSlotConfigurationStore();
        await store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Dirty));
        var resolver = new ConfigurationResolver(store, TestStores.NewSignalHandlerStore(), NullLogger<ConfigurationResolver>.Instance);

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
    public async Task HandleAsync_SlottedHappyPath_PublishesSuccessResponseWithEmptySlotsToCorrectTopic()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var store = TestStores.NewSlotConfigurationStore();
        await store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Valid));
        var resolver = new ConfigurationResolver(store, TestStores.NewSignalHandlerStore(), NullLogger<ConfigurationResolver>.Instance);

        // Manifest must declare the slot so the configuration pre-flight runs.
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

        // The handler additionally publishes a WorkflowStatusEvent to the status exchange;
        // this test only cares about the configuration response.
        var configResponses = bus.Published.Where(p => p.Message is WorkflowConfigurationResponse).ToList();
        Assert.That(configResponses, Has.Count.EqualTo(1));
        var (topic, msg) = configResponses[0];
        Assert.That(topic, Is.EqualTo("my-response-topic"));
        var response = (WorkflowConfigurationResponse)msg;
        Assert.That(response.Success, Is.True);
        // Credentials no longer ship at registration — slots activate just-in-time.
        Assert.That(response.Slots, Is.Empty);
        Assert.That(response.ErrorMessage, Is.Null);
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

        var responses = bus.Published.Where(p => p.Message is WorkflowConfigurationResponse).ToList();
        Assert.That(responses, Has.Count.EqualTo(1));
        var response = (WorkflowConfigurationResponse)responses[0].Message;
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

        var store = TestStores.NewSlotConfigurationStore();
        await store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Valid));
        var resolver = new ConfigurationResolver(store, TestStores.NewSignalHandlerStore(), NullLogger<ConfigurationResolver>.Instance);

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

    // ------------------------------------------------------------------ Long-living lifetime approval

    private static WorkflowRegistrationRequest LongLivingSlotlessRequest(string workflowName)
    {
        var instanceId = Guid.NewGuid();
        return new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest(workflowName, instanceId.ToString(), [], [], string.Empty, [], [])
            {
                Lifetime = WorkflowLifetime.LongLiving
            },
            ValidPublicKey(), "reply-topic");
    }

    [Test]
    public async Task HandleAsync_LongLivingManifest_TypeNotApproved_PublishesFailureResponse()
    {
        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus); // no approved types
        await handler.StartAsync(CancellationToken.None);

        await bus.InvokeAsync(LongLivingSlotlessRequest("ServiceWorkflow"), CancellationToken.None);

        var responses = bus.Published.Where(p => p.Message is WorkflowConfigurationResponse).ToList();
        Assert.That(responses, Has.Count.EqualTo(1));
        var response = (WorkflowConfigurationResponse)responses[0].Message;
        Assert.That(response.Success, Is.False);
        Assert.That(response.ErrorMessage, Does.Contain("operator approval"));
        Assert.That(handler.RegisteredCount, Is.Zero);
    }

    [Test]
    public async Task HandleAsync_LongLivingManifest_TypeApproved_PublishesSuccessResponse()
    {
        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, approvedLongLivingWorkflowTypes: ["ServiceWorkflow"]);
        await handler.StartAsync(CancellationToken.None);

        await bus.InvokeAsync(LongLivingSlotlessRequest("ServiceWorkflow"), CancellationToken.None);

        var responses = bus.Published.Where(p => p.Message is WorkflowConfigurationResponse).ToList();
        Assert.That(responses, Has.Count.EqualTo(1));
        var response = (WorkflowConfigurationResponse)responses[0].Message;
        Assert.That(response.Success, Is.True);
        Assert.That(handler.RegisteredCount, Is.EqualTo(1));
    }

    // ------------------------------------------------------------------ Instance token authentication

    private static WorkflowInstanceTokenRegistry MakeTokenRegistry() =>
        new(Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System);

    private static WorkflowRegistrationRequest SlotlessRequest(Guid instanceId, string? token) =>
        new(instanceId,
            new WorkflowManifest("SlotlessWorkflow", instanceId.ToString(), [], [], string.Empty, [], []),
            ValidPublicKey(), "self-declared-topic", token);

    [Test]
    public async Task HandleAsync_TokenRequired_MissingToken_PublishesNothing()
    {
        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, tokenRegistry: MakeTokenRegistry(), requireInstanceToken: true);
        await handler.StartAsync(CancellationToken.None);

        await bus.InvokeAsync(SlotlessRequest(Guid.NewGuid(), token: null), CancellationToken.None);

        Assert.That(bus.Published, Is.Empty);
        Assert.That(handler.RegisteredCount, Is.Zero);
    }

    [Test]
    public async Task HandleAsync_TokenRequired_WrongToken_PublishesNothing()
    {
        var registry = MakeTokenRegistry();
        var issued = registry.Issue("SlotlessWorkflow");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, tokenRegistry: registry, requireInstanceToken: true);
        await handler.StartAsync(CancellationToken.None);

        await bus.InvokeAsync(
            SlotlessRequest(issued.WorkflowInstanceId, token: "not-the-issued-token"),
            CancellationToken.None);

        Assert.That(bus.Published, Is.Empty);
    }

    [Test]
    public async Task HandleAsync_TokenRequired_ValidToken_RespondsOnCanonicalQueueIgnoringSelfDeclaredTopic()
    {
        var registry = MakeTokenRegistry();
        var issued = registry.Issue("SlotlessWorkflow");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, tokenRegistry: registry, requireInstanceToken: true);
        await handler.StartAsync(CancellationToken.None);

        await bus.InvokeAsync(
            SlotlessRequest(issued.WorkflowInstanceId, issued.Token),
            CancellationToken.None);

        var responses = bus.Published.Where(p => p.Message is WorkflowConfigurationResponse).ToList();
        Assert.That(responses, Has.Count.EqualTo(1));
        var (topic, msg) = responses[0];
        Assert.That(topic, Is.EqualTo(Auxilia.Workflows.Messaging.WorkflowQueues.ResponseQueueFor(issued.WorkflowInstanceId)));
        Assert.That(((WorkflowConfigurationResponse)msg).Success, Is.True);
    }

    [Test]
    public async Task HandleAsync_TokenRequired_TokenIsSingleUse_SecondRegistrationRejected()
    {
        var registry = MakeTokenRegistry();
        var issued = registry.Issue("SlotlessWorkflow");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, tokenRegistry: registry, requireInstanceToken: true);
        await handler.StartAsync(CancellationToken.None);

        await bus.InvokeAsync(SlotlessRequest(issued.WorkflowInstanceId, issued.Token), CancellationToken.None);
        await bus.InvokeAsync(SlotlessRequest(issued.WorkflowInstanceId, issued.Token), CancellationToken.None);

        Assert.That(bus.Published.Where(p => p.Message is WorkflowConfigurationResponse).Count(), Is.EqualTo(1),
            "Second registration with a consumed token must be rejected.");
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

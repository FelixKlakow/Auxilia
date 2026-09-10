using System.Security.Cryptography;
using Auxilia.Messaging;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Environment;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowRegistrationHandlerTests
{
    private static WorkflowRegistrationHandler MakeHandler(
        CapturingFakeMessageBusClient messageBus,
        EnvironmentValidator? envValidator = null,
        WorkflowInstanceRegistry? instanceRegistry = null,
        WorkflowInstanceTokenRegistry? tokenRegistry = null,
        bool requireInstanceToken = false,
        List<string>? approvedLongLivingWorkflowTypes = null,
        WorkflowSchemaStore? schemaStore = null,
        SignalHandlerStore? signalStore = null)
    {
        var profile = new RunnerProfile
        {
            AvailableTools = new HashSet<string>(),
            OpenPorts = new HashSet<int>()
        };
        var validator = envValidator ?? new EnvironmentValidator(
            Options.Create(profile),
            NullLogger<EnvironmentValidator>.Instance);

        var settings = Options.Create(new WorkflowDispatcherSettings
        {
            RequireInstanceToken = requireInstanceToken,
            ApprovedLongLivingWorkflowTypes = approvedLongLivingWorkflowTypes ?? []
        });

        return new WorkflowRegistrationHandler(
            messageBus,
            validator,
            signalStore ?? TestStores.NewSignalHandlerStore(),
            schemaStore ?? TestStores.NewWorkflowSchemaStore(),
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
    public async Task HandleAsync_AcceptedRegistration_PersistsTheManifestSchema()
    {
        var schemaStore = TestStores.NewWorkflowSchemaStore();
        var manifest = new WorkflowManifest(
            "TestWorkflow", Guid.NewGuid().ToString(), [], [], "3.1", ["tag-a"], [])
        {
            Inputs = [new WorkflowInputDescriptor("instruction", "Instruction", Required: true)],
            Triggers = [new TriggerDeclaration(TriggerDeclaration.Manual)],
            ConsumedArtifacts = ["session-report"],
            InteractiveTerminalPort = 7681
        };
        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(), manifest, ValidPublicKey(), "reply-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, schemaStore: schemaStore);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        var schema = await schemaStore.GetSchemaAsync("TestWorkflow");
        Assert.Multiple(() =>
        {
            Assert.That(schema, Is.Not.Null, "the registration manifest is the package's schema of record");
            Assert.That(schema!.Version, Is.EqualTo("3.1"));
            Assert.That(schema.Tags, Is.EqualTo(new[] { "tag-a" }));
            // A run-time registration REPLACES the stored schema: any field dropped here is
            // silently wiped by the workflow's first run (that bug hid the Run-input form).
            Assert.That(schema.Inputs.Select(i => i.Name), Is.EqualTo(new[] { "instruction" }));
            Assert.That(schema.Triggers.Select(t => t.Kind), Is.EqualTo(new[] { TriggerDeclaration.Manual }));
            Assert.That(schema.ConsumedArtifacts, Is.EqualTo(new[] { "session-report" }));
            Assert.That(schema.InteractiveTerminalPort, Is.EqualTo(7681));
        });
    }

    [Test]
    public async Task HandleAsync_RejectedEnvironment_DoesNotPersistASchema()
    {
        var schemaStore = TestStores.NewWorkflowSchemaStore();
        var manifest = new WorkflowManifest(
            "TestWorkflow", Guid.NewGuid().ToString(), [],
            [new ToolRequirement("git")], string.Empty, [], []);
        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(), manifest, ValidPublicKey(), "reply-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, schemaStore: schemaStore);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(await schemaStore.GetSchemaAsync("TestWorkflow"), Is.Null);
    }

    [Test]
    public async Task HandleAsync_SlottedHappyPath_PublishesSuccessResponseWithEmptySlotsToCorrectTopic()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        // A slotted workflow no longer needs stored slot configuration at registration —
        // credentials are resolved just-in-time by the Core when each slot activates.
        var manifest = new WorkflowManifest(
            "TestWorkflow", Guid.NewGuid().ToString(),
            [new SlotDefinition("slotA", null) { ServiceType = typeof(object) }],
            [], string.Empty, [], []);
        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(), manifest, publicKey, "my-response-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus);
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
    public async Task HandleAsync_ResolvesSignalHandlersFromStore_IntoResponse()
    {
        // The registration response carries the workflow type's configured signal handlers so the
        // running instance knows how to react to each signal it emits.
        var signalStore = TestStores.NewSignalHandlerStore();
        await signalStore.UpsertHandlerAsync("SignalWorkflow",
            new StoredSignalHandlerConfiguration("build-complete",
                new NotifySignalHandler("slack", new Dictionary<string, string> { ["channel"] = "#builds" })));

        var instanceId = Guid.NewGuid();
        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("SignalWorkflow", instanceId.ToString(), [], [], string.Empty, [], []),
            ValidPublicKey(), "reply-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, signalStore: signalStore);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        var response = (WorkflowConfigurationResponse)bus.Published
            .First(p => p.Message is WorkflowConfigurationResponse).Message;
        Assert.That(response.Success, Is.True);
        Assert.That(response.SignalHandlers.ContainsKey("build-complete"), Is.True);
        Assert.That(response.SignalHandlers["build-complete"], Is.TypeOf<NotifySignalHandler>());
    }

    [Test]
    public async Task HandleAsync_NoSlots_PublishesSuccessWithEmptySlots()
    {
        // A workflow with no declared slots succeeds immediately.
        var request = new WorkflowRegistrationRequest(
            Guid.NewGuid(),
            new WorkflowManifest("SlotlessWorkflow", Guid.NewGuid().ToString(),
                [], // no slots
                [], string.Empty, [], []),
            ValidPublicKey(),
            "reply-topic");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus);
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
    public async Task HandleAsync_SlottedHappyPath_TracksInstanceId()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var instanceId = Guid.NewGuid();
        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("TestWorkflow", instanceId.ToString(),
                [new SlotDefinition("slotA", null) { ServiceType = typeof(object) }],
                [], string.Empty, [], []),
            publicKey,
            "reply");

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(handler.RegisteredCount, Is.EqualTo(1));
    }

    [Test]
    public async Task HandleAsync_NoSlots_ManifestWithViews_RecordsViewsJsonOnInstanceRecord()
    {
        var instanceId = Guid.NewGuid();
        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("SlotlessWorkflow", instanceId.ToString(),
                [], [], string.Empty, [], [])
            {
                Views = [new Auxilia.Workflows.Views.ViewDescriptor(
                    "progress", "{}",
                    Auxilia.Workflows.Views.ViewRendering.Stream,
                    Auxilia.Workflows.Views.ViewLifecycle.LiveAndPersisted)]
            },
            ValidPublicKey(), "reply");

        var bus = new CapturingFakeMessageBusClient();
        var registry = TestStores.NewWorkflowInstanceRegistry();
        var handler = MakeHandler(bus, instanceRegistry: registry);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        var record = await registry.GetAsync(instanceId);
        Assert.That(record, Is.Not.Null);
        Assert.That(record!.ViewsJson, Does.Contain("progress"),
            "Registration must persist the manifest's view descriptors for later replay.");
    }

    [Test]
    public async Task HandleAsync_ManifestWithCustomView_CarriesRendererKeyIntoViewsJson()
    {
        var instanceId = Guid.NewGuid();
        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("AgentWorkflow", instanceId.ToString(),
                [], [], string.Empty, [], [])
            {
                Views = [new Auxilia.Workflows.Views.ViewDescriptor(
                    "agent-conversation", "{}",
                    Auxilia.Workflows.Views.ViewRendering.Custom,
                    Auxilia.Workflows.Views.ViewLifecycle.LiveAndPersisted,
                    "agent-chat")]
            },
            ValidPublicKey(), "reply");

        var bus = new CapturingFakeMessageBusClient();
        var registry = TestStores.NewWorkflowInstanceRegistry();
        var handler = MakeHandler(bus, instanceRegistry: registry);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        var record = await registry.GetAsync(instanceId);
        Assert.That(record, Is.Not.Null);
        var views = System.Text.Json.JsonSerializer
            .Deserialize<List<Auxilia.Workflows.Views.ViewDescriptor>>(record!.ViewsJson!)!;
        Assert.That(views.Single().RendererKey, Is.EqualTo("agent-chat"),
            "The renderer key must round-trip through the stored ViewsJson.");
    }

    [Test]
    public async Task HandleAsync_Slotted_ManifestWithViews_RecordsViewsJsonOnInstanceRecord()
    {
        var instanceId = Guid.NewGuid();
        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("TestWorkflow", instanceId.ToString(),
                [new SlotDefinition("slotA", null) { ServiceType = typeof(object) }],
                [], string.Empty, [], [])
            {
                Views = [new Auxilia.Workflows.Views.ViewDescriptor(
                    "log", "{}",
                    Auxilia.Workflows.Views.ViewRendering.Log,
                    Auxilia.Workflows.Views.ViewLifecycle.Persisted)]
            },
            ValidPublicKey(), "reply");

        var bus = new CapturingFakeMessageBusClient();
        var registry = TestStores.NewWorkflowInstanceRegistry();
        var handler = MakeHandler(bus, instanceRegistry: registry);
        await handler.StartAsync(CancellationToken.None);
        await bus.InvokeAsync(request, CancellationToken.None);

        var record = await registry.GetAsync(instanceId);
        Assert.That(record, Is.Not.Null);
        Assert.That(record!.ViewsJson, Does.Contain("log"));
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
    public async Task HandleAsync_TokenRequired_ManifestNamesAnotherType_PublishesNothingAndKeepsSchemaUntouched()
    {
        // The token was issued for OtherWorkflow; the manifest claims SlotlessWorkflow and would
        // otherwise rewrite SlotlessWorkflow's schema of record.
        var registry = MakeTokenRegistry();
        var issued = registry.Issue("OtherWorkflow");
        var schemaStore = TestStores.NewWorkflowSchemaStore();

        var bus = new CapturingFakeMessageBusClient();
        var handler = MakeHandler(bus, tokenRegistry: registry, requireInstanceToken: true, schemaStore: schemaStore);
        await handler.StartAsync(CancellationToken.None);

        await bus.InvokeAsync(SlotlessRequest(issued.WorkflowInstanceId, issued.Token), CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(bus.Published, Is.Empty);
            Assert.That(handler.RegisteredCount, Is.Zero);
            Assert.That(await schemaStore.GetSchemaAsync("SlotlessWorkflow"), Is.Null);
            Assert.That(registry.IsRegistered(issued.WorkflowInstanceId), Is.False,
                "A cross-type attempt must not burn the instance's single registration.");
        });
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

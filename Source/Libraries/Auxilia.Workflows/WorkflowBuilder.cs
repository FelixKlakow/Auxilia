using Auxilia.Messaging;
using Auxilia.Workflows.Capabilities;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Environment;
using Auxilia.Workflows.Internal;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Schema;

namespace Auxilia.Workflows;

public sealed class WorkflowBuilder : IWorkflowBuilder
{
    private readonly string _workflowName;
    private readonly List<SlotDefinition> _slots = new();
    private readonly List<IEnvironmentRequirement> _environmentRequirements = new();
    private readonly List<WorkflowOutputDescriptor> _outputs = new();
    private readonly List<Network.NetworkEndpointDeclaration> _networkEndpoints = new();
    private readonly List<Workspace.RepositoryDeclaration> _repositories = new();
    private readonly List<SignalDescriptor> _signals = new();
    private readonly List<Views.ViewDescriptor> _views = new();
    private readonly List<Events.EventDescriptor> _events = new();
    private readonly List<TriggerDeclaration> _triggers = new();
    private readonly List<WorkflowInputDescriptor> _inputs = new();
    private readonly List<string> _consumedArtifacts = new();
    private readonly List<Companions.CompanionDeclaration> _companions = new();
    private Companions.PodControlDeclaration? _podControl;
    private readonly WorkflowMetadata _metadata = new();
    private Action<IServiceCollection>? _configureServices;
    private Func<IServiceProvider, CancellationToken, Task>? _application;
    private WorkflowLifetime _lifetime = WorkflowLifetime.OneShot;
    private int? _interactiveTerminalPort;
    private InteractiveTerminalGate? _interactiveTerminalGate;

    private const string StateQueueName = "workflow.state";
    private const string StateExchangeName = "workflow.state";

    public static IWorkflowRunContext? TestContext { get; set; }
    public static SlotHandlerResolver? TestSlotHandlerResolver { get; set; }

    internal TimeSpan _directiveTimeout = TimeSpan.FromSeconds(30);

    private WorkflowBuilder(string workflowName)
    {
        _workflowName = workflowName;
    }

    public static IWorkflowBuilder Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new WorkflowBuilder(name);
    }

    public IWorkflowBuilder Requires<TService>(
        string name, ICapability capabilities, string? description = null, bool optional = false,
        bool allowMultiple = false, IReadOnlyList<string>? providerTypes = null)
    {
        if (_slots.Any(s => s.SlotName == name))
            throw new InvalidOperationException($"A slot with name '{name}' has already been declared.");
        _slots.Add(new SlotDefinition(name, capabilities, description)
        {
            ServiceType = typeof(TService),
            Contract = typeof(TService).FullName,
            Optional = optional,
            AllowMultiple = allowMultiple,
            ProviderTypes = providerTypes is { Count: > 0 } ? providerTypes : null
        });
        return this;
    }

    public IWorkflowBuilder RequiresEnvironment(Action<IEnvironmentBuilder> configure)
    {
        var builder = new EnvironmentBuilder();
        configure(builder);
        _environmentRequirements.AddRange(builder.Requirements);
        return this;
    }

    public IWorkflowBuilder WithMetadata(Action<WorkflowMetadata> configure)
    {
        configure(_metadata);
        return this;
    }

    public IWorkflowBuilder WithLifetime(WorkflowLifetime lifetime)
    {
        _lifetime = lifetime;
        return this;
    }

    public IWorkflowBuilder DeclaresOutput(string name, string relativePath, string? description = null)
    {
        _outputs.Add(new WorkflowOutputDescriptor(name, relativePath, description));
        return this;
    }

    public IWorkflowBuilder DeclaresTrigger(string kind, string? description = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        if (_triggers.Any(t => t.Kind == kind))
            throw new InvalidOperationException($"Trigger kind '{kind}' has already been declared.");
        _triggers.Add(new TriggerDeclaration(kind, description));
        return this;
    }

    public IWorkflowBuilder RequiresInput(
        string name, string label, bool required = false, string? description = null)
        => RequiresInput(new WorkflowInputDescriptor(name, label, required, description));

    public IWorkflowBuilder RequiresInput(WorkflowInputDescriptor input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(input.Name);
        ArgumentException.ThrowIfNullOrEmpty(input.Label);
        if (_inputs.Any(i => i.Name == input.Name))
            throw new InvalidOperationException($"An input with name '{input.Name}' has already been declared.");
        _inputs.Add(input);
        return this;
    }

    public IWorkflowBuilder ConsumesArtifact(string artifactType)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactType);
        if (!_consumedArtifacts.Contains(artifactType))
            _consumedArtifacts.Add(artifactType);
        return this;
    }

    public IWorkflowBuilder WithInteractiveTerminal(int containerPort = 7681, InteractiveTerminalGate? gate = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(containerPort);
        _interactiveTerminalPort = containerPort;
        _interactiveTerminalGate = gate;
        return this;
    }

    public IWorkflowBuilder RequiresNetworkEndpoint(string endpoint, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        _networkEndpoints.Add(new Network.NetworkEndpointDeclaration(endpoint, purpose));
        return this;
    }

    public IWorkflowBuilder RequiresRepository(
        string id, string cloneUrl, string? branch = null, bool noCache = false,
        string? setupScript = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(cloneUrl);
        if (_repositories.Any(r => r.Id == id))
            throw new InvalidOperationException($"A repository with id '{id}' has already been declared.");
        _repositories.Add(new Workspace.RepositoryDeclaration(id, cloneUrl, branch, noCache)
        {
            SetupScript = setupScript
        });
        return this;
    }

    public IWorkflowBuilder RequiresCompanion(
        string name, string image, Action<Companions.ICompanionBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(image);
        if (_companions.Any(c => c.Name == name))
            throw new InvalidOperationException($"A companion with name '{name}' has already been declared.");
        var builder = new Companions.CompanionBuilder(name, image);
        configure?.Invoke(builder);
        _companions.Add(builder.Build());
        return this;
    }

    public IWorkflowBuilder RequiresPodControl(
        int maxContainers, string? description = null, params string[] podVolumes)
    {
        if (_podControl is not null)
            throw new InvalidOperationException("Pod control has already been declared.");
        _podControl = new Companions.PodControlDeclaration(maxContainers, description)
        {
            PodVolumes = podVolumes
        };
        return this;
    }

    private IReadOnlyList<Companions.CompanionDeclaration> ValidatedCompanions()
    {
        if (_companions.Count == 0 && _podControl is null)
            return _companions.AsReadOnly();
        var errors = Companions.CompanionTopologyValidator.Validate(_companions, _podControl);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Invalid companion topology: " + string.Join(" ", errors));
        return _companions.AsReadOnly();
    }

    public IWorkflowBuilder DeclaresSignal<TPayload>(string name, string? description = null)
    {
        if (_signals.Any(s => s.Name == name))
            throw new InvalidOperationException($"A signal with name '{name}' has already been declared.");
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Default, typeof(TPayload));
        _signals.Add(new SignalDescriptor(name, typeof(TPayload).FullName ?? typeof(TPayload).Name, schema.ToJsonString(), description));
        return this;
    }

    public IWorkflowBuilder DeclaresView<TItem>(
        string name, Views.ViewRendering rendering, Views.ViewLifecycle lifecycle)
        => DeclaresView<TItem>(name, rendering, lifecycle, rendererKey: null);

    public IWorkflowBuilder DeclaresView<TItem>(
        string name, Views.ViewRendering rendering, Views.ViewLifecycle lifecycle, string? rendererKey)
        => DeclaresView<TItem>(name, rendering, lifecycle, rendererKey, declaredData: null);

    public IWorkflowBuilder DeclaresView<TItem>(
        string name, Views.ViewRendering rendering, Views.ViewLifecycle lifecycle,
        string? rendererKey, object? declaredData)
    {
        if (_views.Any(v => v.Name == name))
            throw new InvalidOperationException($"A view with name '{name}' has already been declared.");
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Default, typeof(TItem));
        _views.Add(new Views.ViewDescriptor(name, schema.ToJsonString(), rendering, lifecycle, rendererKey,
            declaredData is null ? null : JsonSerializer.Serialize(declaredData, declaredData.GetType())));
        return this;
    }

    public IWorkflowBuilder DeclaresEvent(string eventType, string? description = null)
        => DeclaresEvent(eventType, payloadSchemaJson: null, description);

    public IWorkflowBuilder DeclaresEvent<TPayload>(string eventType, string? description = null)
        => DeclaresEvent(eventType,
            JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Default, typeof(TPayload)).ToJsonString(),
            description);

    private IWorkflowBuilder DeclaresEvent(string eventType, string? payloadSchemaJson, string? description)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventType);
        if (eventType.StartsWith(WorkflowEventMessage.ReservedPlatformPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Event type '{eventType}' uses the reserved platform prefix " +
                $"'{WorkflowEventMessage.ReservedPlatformPrefix}'.");
        if (_events.Any(e => e.EventType == eventType))
            throw new InvalidOperationException($"Event '{eventType}' has already been declared.");
        _events.Add(new Events.EventDescriptor(eventType, payloadSchemaJson, description));
        return this;
    }

    public IWorkflowBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _configureServices = configure;
        return this;
    }

    public IWorkflowBuilder WithApplication(Func<IServiceProvider, CancellationToken, Task> run)
    {
        _application = run;
        return this;
    }

    public async Task Run(string[] args)
    {
        // Registry support: print the schema and exit without touching the message bus, so
        // deployment tooling can register a package's slots before it ever runs.
        if (args.Contains("--emit-schema"))
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(BuildSchema()));
            return;
        }

        if (args.Contains("--test-harness") && TestContext is { } testCtx)
        {
            await Run(args, testCtx);
            return;
        }

        await using var context = new DefaultWorkflowRunContext(args);
        await Run(args, context);
    }

    public Task RunAsync(string[] args, IWorkflowRunContext context) => Run(args, context);

    public async Task Run(string[] args, IWorkflowRunContext context)
    {
        using var keyPair = new EphemeralKeyPair();

        // Platform-launched instances receive their identity and a one-time token at launch;
        // a self-generated identity is the unauthenticated dev fallback.
        var instanceId = Guid.TryParse(
            System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceId),
            out var assignedId) ? assignedId : Guid.NewGuid();
        var instanceToken = System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceToken);
        var responseTopic = WorkflowQueues.ResponseQueueFor(instanceId);

        await context.MessageBus.DeclareQueueAsync(responseTopic);

        var directiveTcs = new TaskCompletionSource<WorkflowDirective>();
        var directiveSub = await context.MessageBus.SubscribeAsync<WorkflowDirective>(
            responseTopic, (msg, _) => { directiveTcs.TrySetResult(msg); return Task.CompletedTask; });

        await context.MessageBus.PublishAsync(
            System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.AnnouncementQueue) ?? "workflow.announcements",
            new WorkflowAnnouncementMessage(instanceId, _workflowName, keyPair.PublicKeyBase64, responseTopic, instanceToken));

        var completed = await Task.WhenAny(directiveTcs.Task, Task.Delay(_directiveTimeout));
        await directiveSub.DisposeAsync();

        if (completed != directiveTcs.Task)
        {
            context.Logger.LogError("Timed out waiting for WorkflowDirective.");
            context.ExitService.Exit(1);
            return;
        }

        var directive = directiveTcs.Task.Result;

        switch (directive.Directive)
        {
            case WorkflowDirectiveKind.Run:
            {
                var configTcs = new TaskCompletionSource<WorkflowConfigurationResponse>();
                var configSub = await context.MessageBus.SubscribeAsync<WorkflowConfigurationResponse>(
                    responseTopic, (msg, _) => { configTcs.TrySetResult(msg); return Task.CompletedTask; });

                await context.MessageBus.PublishAsync(
                    System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.RegistrationQueue) ?? "workflow-registration",
                    new WorkflowRegistrationRequest(instanceId, BuildManifest(instanceId),
                        keyPair.PublicKeyBase64, responseTopic, instanceToken));

                var configCompleted = await Task.WhenAny(configTcs.Task, Task.Delay(_directiveTimeout));
                await configSub.DisposeAsync();

                if (configCompleted != configTcs.Task)
                {
                    context.Logger.LogError("Timed out waiting for WorkflowConfigurationResponse.");
                    context.ExitService.Exit(1);
                    return;
                }

                var response = configTcs.Task.Result;
                if (!response.Success)
                {
                    context.Logger.LogError("WorkflowConfigurationResponse indicated failure: {Error}", response.ErrorMessage);
                    context.ExitService.Exit(1);
                    return;
                }

                await context.MessageBus.DeclareExchangeAsync(StateExchangeName);
                await context.MessageBus.DeclareQueueAsync("workflow.signals");

                var cancelQueueName = $"workflow-cancel-{instanceId}";
                await context.MessageBus.DeclareQueueAsync(cancelQueueName);
                using var cts = new CancellationTokenSource();
                var cancelSub = await context.MessageBus.SubscribeAsync<CancelWorkflowCommand>(
                    cancelQueueName, (_, _) => { cts.Cancel(); return Task.CompletedTask; });

                // Steer-back inputs: the Core's deliver-input endpoint publishes opaque payloads to
                // this instance's INPUT queue; the application awaits them via IWorkflowInputs.
                var inputQueue = WorkflowQueues.InputQueueFor(instanceId);
                await context.MessageBus.DeclareQueueAsync(inputQueue);
                var workflowInputs = new ChannelWorkflowInputs();
                var inputSub = await context.MessageBus.SubscribeAsync<Messaging.Messages.WorkflowInputMessage>(
                    inputQueue, (msg, _) => { workflowInputs.Push(msg.PayloadJson); return Task.CompletedTask; });

                using var drainSignal = new WorkflowDrainSignal();
                IAsyncDisposable? drainSub = null;
                if (_lifetime == WorkflowLifetime.LongLiving)
                {
                    var drainQueueName = $"workflow-drain-{instanceId}";
                    await context.MessageBus.DeclareQueueAsync(drainQueueName);
                    drainSub = await context.MessageBus.SubscribeAsync<DrainWorkflowCommand>(
                        drainQueueName, (_, _) => { drainSignal.SignalDrain(); return Task.CompletedTask; });
                }

                SlotActivator? slotActivator = null;
                ResourceProxyClient? resourceProxyClient = null;
                Companions.PodControlClient? podControlClient = null;
                try
                {
                    // Post-binding workspace setup (ARCHITECTURE §9): declared repository setup
                    // scripts and mount-announced 'setup-script' roles run here, inside the
                    // container, before anything of the application — fail-fast on a non-zero exit.
                    await Workspace.WorkspaceSetup.RunDeclaredAsync(
                        _repositories.AsReadOnly(), context.Logger, cts.Token);

                    resourceProxyClient = new ResourceProxyClient(
                        context.MessageBus, instanceId, instanceToken,
                        System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.ResourceProxyQueue)
                            ?? "workflow-resource-proxy",
                        WorkflowQueues.ResourceResponseQueueFor(instanceId));
                    await resourceProxyClient.StartAsync();

                    // Runtime pod control: only for runs whose manifest declares the envelope
                    // AND that the platform launched with a pod-control queue (a dev-mode run
                    // without one simply gets no IPodController).
                    if (_podControl is not null
                        && System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.PodControlQueue)
                            is { Length: > 0 } podControlQueue)
                    {
                        podControlClient = new Companions.PodControlClient(
                            context.MessageBus, instanceId, instanceToken, podControlQueue,
                            WorkflowQueues.PodControlResponseQueueFor(instanceId));
                        await podControlClient.StartAsync();
                    }

                    ISlotHandlerResolver? activeResolver = TestSlotHandlerResolver;
                    if (activeResolver is null && TestContext == null)
                    {
                        var resolver = new SlotHandlerResolver();
                        var devMode = new EnvironmentDeveloperModeProvider();
                        var logger = NullLoggerFactory.Instance.CreateLogger<PluginManifestVerifier>();
                        var verifier = new PluginManifestVerifier(devMode, logger);
                        var discovery = new FileSystemPluginDiscovery();
                        var plugins = discovery.DiscoverPlugins(AppContext.BaseDirectory);
                        var loader = new PluginLoader(resolver, verifier);
                        loader.Load(plugins);
                        activeResolver = resolver;
                    }

                    var services = new ServiceCollection();
                    services.AddSingleton(context.MessageBus);
                    services.AddSingleton<IWorkflowInputs>(workflowInputs);
                    services.AddSingleton<IRunInputs>(new EnvironmentRunInputs(_inputs.AsReadOnly()));
                    services.AddSingleton(drainSignal);
                    services.AddSingleton(resourceProxyClient);
                    if (podControlClient is not null)
                        services.AddSingleton<Companions.IPodController>(podControlClient);
                    services.AddSingleton(new Views.DeclaredViews(_views.AsReadOnly()));
                    services.AddSingleton<Views.IViewPublisher>(
                        new Views.DefaultViewPublisher(context.MessageBus, instanceId, _views.AsReadOnly()));
                    services.AddSingleton<Events.IEventPublisher>(
                        new Events.DefaultEventPublisher(
                            context.MessageBus, instanceId, _workflowName, _events.AsReadOnly()));
                    if (activeResolver is not null)
                    {
                        // Empty Slots on a successful response means just-in-time delivery:
                        // each slot's configuration is fetched via an individual, audited
                        // activation request — never as a bundle at registration.
                        IReadOnlyDictionary<string, SlotConfiguration>? activatedConfigs = null;
                        if (response.Slots.Count == 0 && _slots.Count > 0)
                        {
                            slotActivator = new SlotActivator(
                                context.MessageBus, keyPair, instanceId, instanceToken,
                                System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.SlotActivationQueue)
                                    ?? "workflow-slot-activation",
                                responseTopic);
                            await slotActivator.StartAsync();

                            var fetched = new Dictionary<string, SlotConfiguration>(_slots.Count);
                            foreach (var slot in _slots)
                            {
                                try
                                {
                                    fetched[slot.SlotName] = await slotActivator.FetchAsync(slot.SlotName, cts.Token);
                                }
                                catch (InvalidOperationException) when (slot.Optional)
                                {
                                    // An unbound optional slot is a valid configuration — the run
                                    // proceeds without the capability (its contract resolves to null).
                                }
                            }
                            activatedConfigs = fetched;
                        }

                        new WorkflowBootstrapper(
                            response, keyPair, activeResolver, _slots.AsReadOnly(), instanceId, activatedConfigs)
                            .Apply(services);
                    }

                    _configureServices?.Invoke(services);

                    await using (var provider = services.BuildServiceProvider())
                    {
                        if (_application != null && (TestContext == null || TestSlotHandlerResolver != null))
                            await _application(provider, cts.Token);
                    }

                    await context.MessageBus.PublishToExchangeAsync(StateExchangeName,
                        new WorkflowStateMessage(instanceId, WorkflowState.Success, null));
                    context.ExitService.Exit(0);
                }
                catch (OperationCanceledException)
                {
                    await context.MessageBus.PublishToExchangeAsync(StateExchangeName,
                        new WorkflowStateMessage(instanceId, WorkflowState.Cancelled, null));
                    context.ExitService.Exit(0);
                    return;
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(ex, "Workflow run failed: {Message}", ex.Message);
                    await context.MessageBus.PublishToExchangeAsync(StateExchangeName,
                        new WorkflowStateMessage(instanceId, WorkflowState.Failed, ex.Message));
                    context.ExitService.Exit(1);
                }
                finally
                {
                    await cancelSub.DisposeAsync();
                    await inputSub.DisposeAsync();
                    if (drainSub is not null)
                        await drainSub.DisposeAsync();
                    if (slotActivator is not null)
                        await slotActivator.DisposeAsync();
                    if (resourceProxyClient is not null)
                        await resourceProxyClient.DisposeAsync();
                    if (podControlClient is not null)
                        await podControlClient.DisposeAsync();
                }
                return;
            }

            default:
                context.Logger.LogError("Received unrecognised WorkflowDirective: {Directive}", directive.Directive);
                context.ExitService.Exit(1);
                return;
        }
    }

    public WorkflowSchema BuildSchema()
        => new(_workflowName, _slots.AsReadOnly(), _environmentRequirements.AsReadOnly())
        {
            Version = _metadata.Version,
            Tags = _metadata.Tags.ToList().AsReadOnly(),
            Outputs = _outputs.AsReadOnly(),
            Signals = _signals.AsReadOnly(),
            Lifetime = _lifetime,
            Views = _views.AsReadOnly(),
            NetworkEndpoints = _networkEndpoints.AsReadOnly(),
            Repositories = _repositories.AsReadOnly(),
            Triggers = _triggers.AsReadOnly(),
            Events = _events.AsReadOnly(),
            Inputs = _inputs.AsReadOnly(),
            ConsumedArtifacts = _consumedArtifacts.AsReadOnly(),
            InteractiveTerminalPort = _interactiveTerminalPort,
            InteractiveTerminalGate = _interactiveTerminalGate,
            Companions = ValidatedCompanions(),
            PodControl = _podControl
        };

    internal WorkflowManifest BuildManifest(Guid instanceId = default)
        => new(_workflowName, instanceId.ToString("D"), _slots.AsReadOnly(), _environmentRequirements.AsReadOnly(),
            _metadata.Version, _metadata.Tags.ToList().AsReadOnly(), _outputs.AsReadOnly())
        {
            Signals = _signals.AsReadOnly(),
            Lifetime = _lifetime,
            Views = _views.AsReadOnly(),
            NetworkEndpoints = _networkEndpoints.AsReadOnly(),
            Repositories = _repositories.AsReadOnly(),
            Triggers = _triggers.AsReadOnly(),
            Events = _events.AsReadOnly(),
            Inputs = _inputs.AsReadOnly(),
            ConsumedArtifacts = _consumedArtifacts.AsReadOnly(),
            InteractiveTerminalPort = _interactiveTerminalPort,
            InteractiveTerminalGate = _interactiveTerminalGate,
            Companions = ValidatedCompanions(),
            PodControl = _podControl
        };
}

using Auxilia.Messaging;
using Auxilia.Workflows.Capabilities;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Environment;
using Auxilia.Workflows.Internal;
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
    private readonly List<SignalDescriptor> _signals = new();
    private readonly WorkflowMetadata _metadata = new();
    private Action<IServiceCollection>? _configureServices;
    private Func<IServiceProvider, CancellationToken, Task>? _application;

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

    public IWorkflowBuilder Requires<TService>(string name, ICapability capabilities, string? description = null)
    {
        if (_slots.Any(s => s.SlotName == name))
            throw new InvalidOperationException($"A slot with name '{name}' has already been declared.");
        _slots.Add(new SlotDefinition(name, capabilities, description) { ServiceType = typeof(TService) });
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

    public IWorkflowBuilder DeclaresOutput(string name, string relativePath, string? description = null)
    {
        _outputs.Add(new WorkflowOutputDescriptor(name, relativePath, description));
        return this;
    }

    public IWorkflowBuilder DeclaresSignal<TPayload>(string name, string? description = null)
    {
        if (_signals.Any(s => s.Name == name))
            throw new InvalidOperationException($"A signal with name '{name}' has already been declared.");
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Default, typeof(TPayload));
        _signals.Add(new SignalDescriptor(name, typeof(TPayload).FullName ?? typeof(TPayload).Name, schema.ToJsonString(), description));
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
        var instanceId = Guid.NewGuid();
        var responseTopic = $"workflow-response-{instanceId}";

        await context.MessageBus.DeclareQueueAsync(responseTopic);

        var directiveTcs = new TaskCompletionSource<WorkflowDirective>();
        var directiveSub = await context.MessageBus.SubscribeAsync<WorkflowDirective>(
            responseTopic, (msg, _) => { directiveTcs.TrySetResult(msg); return Task.CompletedTask; });

        await context.MessageBus.PublishAsync("workflow.announcements",
            new WorkflowAnnouncementMessage(instanceId, _workflowName, keyPair.PublicKeyBase64, responseTopic));

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
            case WorkflowDirectiveKind.EmitSchema:
                await context.MessageBus.PublishAsync("workflow.schema",
                    new WorkflowSchemaMessage(instanceId, BuildSchema()));
                context.ExitService.Exit(0);
                return;

            case WorkflowDirectiveKind.Run:
            {
                var configTcs = new TaskCompletionSource<WorkflowConfigurationResponse>();
                var configSub = await context.MessageBus.SubscribeAsync<WorkflowConfigurationResponse>(
                    responseTopic, (msg, _) => { configTcs.TrySetResult(msg); return Task.CompletedTask; });

                await context.MessageBus.PublishAsync("workflow-registration",
                    new WorkflowRegistrationRequest(instanceId, BuildManifest(instanceId),
                        keyPair.PublicKeyBase64, responseTopic));

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

                try
                {
                    var services = new ServiceCollection();
                    services.AddSingleton(context.MessageBus);
                    if (TestSlotHandlerResolver is { } testResolver)
                        new WorkflowBootstrapper(response, keyPair, testResolver, _slots.AsReadOnly(), instanceId).Apply(services);
                    else if (TestContext == null)
                    {
                        var resolver = new SlotHandlerResolver();
                        var devMode = new EnvironmentDeveloperModeProvider();
                        var logger = NullLoggerFactory.Instance.CreateLogger<PluginManifestVerifier>();
                        var verifier = new PluginManifestVerifier(devMode, logger);
                        var discovery = new FileSystemPluginDiscovery();
                        var plugins = discovery.DiscoverPlugins(AppContext.BaseDirectory);
                        var loader = new PluginLoader(resolver, verifier);
                        loader.Load(plugins);
                        new WorkflowBootstrapper(response, keyPair, resolver, _slots.AsReadOnly(), instanceId).Apply(services);
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
                }
                return;
            }

            default:
                context.Logger.LogError("Received unrecognised WorkflowDirective: {Directive}", directive.Directive);
                context.ExitService.Exit(1);
                return;
        }
    }

    internal WorkflowSchema BuildSchema()
        => new(_workflowName, _slots.AsReadOnly(), _environmentRequirements.AsReadOnly())
        {
            Version = _metadata.Version,
            Tags = _metadata.Tags.ToList().AsReadOnly(),
            Outputs = _outputs.AsReadOnly(),
            Signals = _signals.AsReadOnly()
        };

    internal WorkflowManifest BuildManifest(Guid instanceId = default)
        => new(_workflowName, instanceId.ToString("D"), _slots.AsReadOnly(), _environmentRequirements.AsReadOnly(),
            _metadata.Version, _metadata.Tags.ToList().AsReadOnly(), _outputs.AsReadOnly())
        {
            Signals = _signals.AsReadOnly()
        };
}

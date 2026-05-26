using Auxilia.Messaging;
using Auxilia.Workflows.Capabilities;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Environment;
using Auxilia.Workflows.Internal;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows;

public sealed class WorkflowBuilder : IWorkflowBuilder
{
    private readonly string _workflowName;
    private readonly List<SlotDefinition> _slots = new();
    private readonly List<IEnvironmentRequirement> _environmentRequirements = new();
    private readonly List<WorkflowOutputDescriptor> _outputs = new();
    private readonly WorkflowMetadata _metadata = new();
    private Func<IServiceProvider, CancellationToken, Task>? _runBody;

    private const string StateQueueName = "workflow.state";

    public static IWorkflowRunContext? TestContext { get; set; }

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

    public IWorkflowBuilder Requires<T>(string name, T capabilities, string? description = null)
        where T : ICapability
    {
        if (_slots.Any(s => s.SlotName == name))
            throw new InvalidOperationException($"A slot with name '{name}' has already been declared.");
        _slots.Add(new SlotDefinition(name, capabilities, description));
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

    public IWorkflowBuilder WithRunBody(Func<IServiceProvider, CancellationToken, Task> body)
    {
        _runBody = body;
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

                await context.MessageBus.DeclareQueueAsync(StateQueueName);

                try
                {
                    var services = new ServiceCollection();
                    if (TestContext == null)
                        new WorkflowBootstrapper(response, keyPair, new SlotHandlerResolver()).Apply(services);
                    await using (var sp = services.BuildServiceProvider())
                    {
                        if (_runBody is not null)
                            await _runBody(sp, CancellationToken.None);
                    }

                    await context.MessageBus.PublishAsync(StateQueueName,
                        new WorkflowStateMessage(instanceId, WorkflowState.Success, null));
                    context.ExitService.Exit(0);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(ex, "Workflow run failed: {Message}", ex.Message);
                    await context.MessageBus.PublishAsync(StateQueueName,
                        new WorkflowStateMessage(instanceId, WorkflowState.Failed, ex.Message));
                    context.ExitService.Exit(1);
                }
                return;

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
            Outputs = _outputs.AsReadOnly()
        };

    internal WorkflowManifest BuildManifest(Guid instanceId = default)
        => new(_workflowName, instanceId.ToString("D"), _slots.AsReadOnly(), _environmentRequirements.AsReadOnly(),
            _metadata.Version, _metadata.Tags.ToList().AsReadOnly(), _outputs.AsReadOnly());
}

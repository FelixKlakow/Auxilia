using Auxilia.Workflows.Capabilities;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows;

public interface IWorkflowBuilder
{
    /// <summary>
    /// Declares a capability slot. The contract type name of <typeparamref name="TService"/> is
    /// published in the schema so configuration tooling offers only matching providers; an
    /// <paramref name="optional"/> slot may stay unbound in a workflow configuration.
    /// <paramref name="providerTypes"/> narrows the contract match to the providers the workflow's
    /// package can actually execute (e.g. the one CLI bundled in its image); null admits any
    /// provider implementing the contract.
    /// </summary>
    IWorkflowBuilder Requires<TService>(
        string name, ICapability capabilities, string? description = null, bool optional = false,
        bool allowMultiple = false, IReadOnlyList<string>? providerTypes = null);

    IWorkflowBuilder RequiresEnvironment(Action<IEnvironmentBuilder> configure);

    IWorkflowBuilder WithMetadata(Action<WorkflowMetadata> configure);

    /// <summary>
    /// Declares the workflow's lifetime. Long-living workflows receive a
    /// <see cref="WorkflowDrainSignal"/> via DI and must honour it; deployment additionally
    /// requires operator approval on the Core.Runner.
    /// </summary>
    IWorkflowBuilder WithLifetime(WorkflowLifetime lifetime);

    IWorkflowBuilder DeclaresOutput(string name, string relativePath, string? description = null);

    /// <summary>
    /// Declares a trigger kind (<see cref="TriggerDeclaration"/>) this workflow is designed to
    /// be started by. The trigger itself lives outside the workflow — the declaration guides
    /// configurators to wire one.
    /// </summary>
    IWorkflowBuilder DeclaresTrigger(string kind, string? description = null);

    /// <summary>
    /// Declares a run input (<see cref="WorkflowInputDescriptor"/>) this workflow reads from
    /// its dispatch context. Dispatch UIs render declared inputs generically; declaring no
    /// required input lets runs start without any.
    /// </summary>
    IWorkflowBuilder RequiresInput(
        string name, string label, bool required = false, string? description = null);

    /// <summary>
    /// Declares a run input with its full descriptor — rendering kind, default value, and
    /// choices included (see <see cref="WorkflowInputDescriptor"/>).
    /// </summary>
    IWorkflowBuilder RequiresInput(WorkflowInputDescriptor input);

    /// <summary>
    /// Declares an artifact type this workflow can process as input — the criteria used when
    /// chaining it after another workflow's typed output.
    /// </summary>
    IWorkflowBuilder ConsumesArtifact(string artifactType);

    /// <summary>
    /// Declares the container port of the workflow's interactive web terminal (ttyd). An
    /// optional <paramref name="gate"/> makes the terminal per-run: it is exposed only when
    /// the gate's input resolves to its enabling value (see <see cref="InteractiveTerminalGate"/>).
    /// </summary>
    IWorkflowBuilder WithInteractiveTerminal(int containerPort = 7681, InteractiveTerminalGate? gate = null);

    /// <summary>
    /// Declares a network endpoint this workflow needs to reach directly (ARCHITECTURE §10).
    /// Declarations form the signed baseline of the run's effective network policy.
    /// </summary>
    IWorkflowBuilder RequiresNetworkEndpoint(string endpoint, string purpose);

    /// <summary>
    /// Declares a repository this workflow needs in its workspace (ARCHITECTURE §9); the
    /// platform prepares a per-run copy at <c>/workspace/repos/&lt;id&gt;</c> before launch.
    /// </summary>
    IWorkflowBuilder RequiresRepository(
        string id, string cloneUrl, string? branch = null, bool noCache = false);

    IWorkflowBuilder DeclaresSignal<TPayload>(string name, string? description = null);

    /// <summary>
    /// Declares a named, schema-declared view (ARCHITECTURE §15). Items published via
    /// <see cref="Views.IViewPublisher"/> must conform to <typeparamref name="TItem"/>.
    /// </summary>
    IWorkflowBuilder DeclaresView<TItem>(
        string name, Views.ViewRendering rendering, Views.ViewLifecycle lifecycle);

    /// <summary>
    /// Declares a view rendered by a named dashboard renderer plug-in
    /// (<paramref name="rendererKey"/>; implies <see cref="Views.ViewRendering.Custom"/> semantics).
    /// </summary>
    IWorkflowBuilder DeclaresView<TItem>(
        string name, Views.ViewRendering rendering, Views.ViewLifecycle lifecycle, string? rendererKey);

    /// <summary>
    /// Declares a view WITH packaging-time data (<see cref="Views.ViewDescriptor.DeclaredDataJson"/>):
    /// presentation content that exists before any run — e.g. the "flow" view's declared steps
    /// (<see cref="Views.FlowStepDescriptor"/>) rendered as a stage view at configuration time.
    /// The data's meaning belongs to the renderer named by <paramref name="rendererKey"/>.
    /// </summary>
    IWorkflowBuilder DeclaresView<TItem>(
        string name, Views.ViewRendering rendering, Views.ViewLifecycle lifecycle,
        string? rendererKey, object? declaredData);

    IWorkflowBuilder ConfigureServices(Action<IServiceCollection> configure);

    IWorkflowBuilder WithApplication(Func<IServiceProvider, CancellationToken, Task> run);

    Task Run(string[] args);

    Task Run(string[] args, IWorkflowRunContext context);

    Task RunAsync(string[] args, IWorkflowRunContext context);

    WorkflowSchema BuildSchema();
}

using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows;

public sealed record WorkflowManifest(
    string WorkflowName,
    string WorkflowInstanceId,
    IReadOnlyList<SlotDefinition> Slots,
    IReadOnlyList<IEnvironmentRequirement> EnvironmentRequirements,
    string Version,
    IReadOnlyList<string> Tags,
    IReadOnlyList<WorkflowOutputDescriptor> Outputs)
{
    public IReadOnlyList<SignalDescriptor> Signals { get; init; } = [];
    public WorkflowLifetime Lifetime { get; init; } = WorkflowLifetime.OneShot;
    public IReadOnlyList<Views.ViewDescriptor> Views { get; init; } = [];
    public IReadOnlyList<Views.FlowStepDescriptor> Flow { get; init; } = [];
    public IReadOnlyList<Network.NetworkEndpointDeclaration> NetworkEndpoints { get; init; } = [];
    public IReadOnlyList<Workspace.RepositoryDeclaration> Repositories { get; init; } = [];
    public IReadOnlyList<TriggerDeclaration> Triggers { get; init; } = [];
    public IReadOnlyList<WorkflowInputDescriptor> Inputs { get; init; } = [];
    public IReadOnlyList<string> ConsumedArtifacts { get; init; } = [];

    /// <summary>Container port of the interactive web terminal, when the workflow hosts one.</summary>
    public int? InteractiveTerminalPort { get; init; }

    /// <summary>Per-run terminal gate (see <see cref="Workflows.InteractiveTerminalGate"/>), when declared.</summary>
    public InteractiveTerminalGate? InteractiveTerminalGate { get; init; }
}

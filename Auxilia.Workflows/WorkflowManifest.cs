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
}

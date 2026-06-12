using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows;

public sealed record WorkflowSchema(
    string WorkflowName,
    IReadOnlyList<SlotDefinition> Slots,
    IReadOnlyList<IEnvironmentRequirement> EnvironmentRequirements)
{
    public string SchemaVersion { get; init; } = "1.0";
    public string Version { get; init; } = string.Empty;
    public WorkflowLifetime Lifetime { get; init; } = WorkflowLifetime.OneShot;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<WorkflowOutputDescriptor> Outputs { get; init; } = [];
    public IReadOnlyList<SignalDescriptor> Signals { get; init; } = [];
    public IReadOnlyList<Views.ViewDescriptor> Views { get; init; } = [];
}

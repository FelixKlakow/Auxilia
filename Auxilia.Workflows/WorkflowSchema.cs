using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows;

public sealed record WorkflowSchema(
    string WorkflowName,
    IReadOnlyList<SlotDefinition> Slots,
    IReadOnlyList<IEnvironmentRequirement> EnvironmentRequirements)
{
    public string SchemaVersion { get; init; } = "1.0";
    public string Version { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<WorkflowOutputDescriptor> Outputs { get; init; } = [];
}

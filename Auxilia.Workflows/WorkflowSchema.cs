using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows;

public sealed record WorkflowSchema(
    string WorkflowName,
    IReadOnlyList<SlotDefinition> Slots,
    IReadOnlyList<IEnvironmentRequirement> EnvironmentRequirements)
{
    public string SchemaVersion { get; init; } = "1.0";
}

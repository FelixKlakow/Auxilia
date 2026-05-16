using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows;

public sealed record WorkflowManifest(
    string WorkflowName,
    string WorkflowInstanceId,
    IReadOnlyList<SlotDefinition> Slots,
    IReadOnlyList<IEnvironmentRequirement> EnvironmentRequirements);

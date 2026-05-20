using Auxilia.Workflows.TaskSource;

namespace Auxilia.ImplementationWorkflow.Context;

public sealed record ImplementationContext(
    WorkItem WorkItem,
    string InstructionsContent,
    string BranchName,
    string WorkingPath);

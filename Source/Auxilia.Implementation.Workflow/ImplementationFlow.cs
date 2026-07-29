using Auxilia.Workflows.Views;

namespace Auxilia.Implementation.Workflow;

/// <summary>
/// The declared step flow — the ONE source of the pipeline's shape: the schema carries it (the
/// steering client's stage view at configuration time), the pipeline publishes runtime states against it.
/// </summary>
public static class ImplementationFlow
{
    public static readonly IReadOnlyList<FlowStepDescriptor> Steps =
    [
        new("workspace", "Workspace", "Branch off the story's repository."),
        new("completeness", "Completeness",
            "Assess the story first; open questions become an operator form.",
            SkipInput: "completeness-check", SkipValue: "false"),
        new("plan", "Plan",
            "Draft the implementation plan — AI-reviewed and gated by your approval when enabled."),
        new("implement", "Implement",
            "Implement the approved plan in the SAME console — AI code review and your approval "
            + "gate when enabled."),
        new("push", "Push",
            "The workflow commits and pushes — the agent never touches git.",
            SkipInput: "push-mode", SkipValue: PushModes.Skip),
        new("story-state", "Story state",
            "Set the story's state from the source's own vocabulary."),
    ];
}

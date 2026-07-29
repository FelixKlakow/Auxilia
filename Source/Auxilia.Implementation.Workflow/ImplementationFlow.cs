using Auxilia.Workflows.Views;

namespace Auxilia.Implementation.Workflow;

/// <summary>
/// The declared step flow — the "flow" view's packaging-time data and the ONE source of the
/// pipeline's shape. Each step owns its inputs AND capability slots, so the steering client filters
/// the configuration form per stage (a selected or skipped stage shows/hides exactly its
/// concerns). Inputs not named by any step are run-wide.
/// </summary>
public static class ImplementationFlow
{
    public static readonly IReadOnlyList<FlowStepDescriptor> Steps =
    [
        new("workspace", "Workspace", "Branch off the story's repository.",
            Inputs: ["work-item-id"],
            Slots: ["repository", "work-items"]),
        new("completeness", "Completeness",
            "Assess the story first; open questions become an operator form.",
            SkipInput: "completeness-check", SkipValue: "false",
            Inputs: ["completeness-check"],
            Slots: ["work-items", "coding-agent"]),
        new("plan", "Plan",
            "Draft the implementation plan — AI-reviewed and gated by your approval when enabled.",
            Inputs: ["user-plan-gate", "ai-review", "max-ai-review-rounds",
                     "reviewer-mode", "reviewer-base-prompt", "author-base-prompt"],
            Slots: ["coding-agent", "review-agent"]),
        new("implement", "Implement",
            "Implement the approved plan in the SAME console — AI code review and your approval "
            + "gate when enabled.",
            Inputs: ["user-code-gate", "artifact-review", "ai-review", "max-ai-review-rounds",
                     "reviewer-mode", "reviewer-base-prompt", "author-base-prompt",
                     "gate-idle-compaction"],
            Slots: ["coding-agent", "review-agent", "repository", "environment"]),
        new("finalization", "Finalization",
            "Wrap up: the workflow commits and pushes (the agent never touches git) and sets "
            + "the story's state from the source's own vocabulary.",
            Inputs: ["push-mode", "target-state"],
            Slots: ["repository", "work-items"]),
    ];
}

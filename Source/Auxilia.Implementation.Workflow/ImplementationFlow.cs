using Auxilia.Workflows.Views;

namespace Auxilia.Implementation.Workflow;

/// <summary>
/// The declared step flow — the "flow" view's packaging-time data and the ONE source of the
/// pipeline's shape: the steering client renders it as the stage view at configuration time (each
/// step owning its inputs, skipped steps hiding them), the pipeline publishes runtime states
/// against it. Inputs not named by any step are run-wide and always visible.
/// </summary>
public static class ImplementationFlow
{
    public static readonly IReadOnlyList<FlowStepDescriptor> Steps =
    [
        new("workspace", "Workspace", "Branch off the story's repository."),
        new("completeness", "Completeness",
            "Assess the story first; open questions become an operator form.",
            SkipInput: "completeness-check", SkipValue: "false",
            Inputs: ["completeness-check"]),
        new("plan", "Plan",
            "Draft the implementation plan — AI-reviewed and gated by your approval when enabled.",
            Inputs: ["user-plan-gate", "ai-review", "max-ai-review-rounds",
                     "reviewer-mode", "reviewer-base-prompt"]),
        new("implement", "Implement",
            "Implement the approved plan in the SAME console — AI code review and your approval "
            + "gate when enabled.",
            Inputs: ["user-code-gate", "artifact-review", "ai-review", "max-ai-review-rounds",
                     "reviewer-mode", "reviewer-base-prompt"]),
        new("push", "Push",
            "The workflow commits and pushes — the agent never touches git.",
            SkipInput: "push-mode", SkipValue: PushModes.Skip,
            Inputs: ["push-mode"]),
        new("story-state", "Story state",
            "Set the story's state from the source's own vocabulary.",
            Inputs: ["target-state"]),
    ];
}

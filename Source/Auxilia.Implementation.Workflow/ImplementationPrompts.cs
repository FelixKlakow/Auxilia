namespace Auxilia.Implementation.Workflow;

/// <summary>
/// The default per-stage instructions — what each drive tells the agent to DO. They are the
/// schema defaults of the matching inputs, so the operator sees and can rewrite them per
/// configuration; the pipeline appends the fixed file-exchange contract (paths under
/// <c>.auxilia/</c>) which is never user-editable.
/// </summary>
public static class ImplementationPrompts
{
    public const string Refinement =
        "Assess whether the user story is complete and unambiguous enough to implement: "
        + "acceptance criteria, affected areas, edge cases, and open dependencies. "
        + "Do not plan or implement yet.";

    public const string Plan =
        "Create a detailed implementation plan (markdown) for the story: approach, files to "
        + "touch, tests, and risks. Include a '## Design' section that describes the change "
        + "with a mermaid diagram (flow or architecture). Do NOT implement yet.";

    public const string Implement =
        "Implement the approved plan point by point and run the tests you touch. "
        + "Commit NOTHING — the workflow handles git.";

    public const string Review =
        "Review critically and concretely: correctness, completeness against the story, "
        + "tests, and risks. Name specific files and findings; do not rubber-stamp.";
}

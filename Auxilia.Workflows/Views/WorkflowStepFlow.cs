namespace Auxilia.Workflows.Views;

/// <summary>
/// One step of a workflow's DECLARED flow (packaging time): the pipeline's shape, carried in the
/// schema so dispatch/config UIs render the stage view before any run exists. The optional
/// <paramref name="SkipInput"/>/<paramref name="SkipValue"/> pair is a data-driven skip hint —
/// editors present the step as skipped when the effective value of that run input equals the
/// value; the platform never learns what either means.
/// </summary>
public sealed record FlowStepDescriptor(
    string Id,
    string Label,
    string? Description = null,
    string? SkipInput = null,
    string? SkipValue = null);

/// <summary>Runtime state of one declared step; states are an open vocabulary
/// ("pending", "active", "done", "skipped" by convention).</summary>
public sealed record WorkflowStepState(string Id, string State);

/// <summary>
/// A FULL snapshot of the run's step STATES, published on the <see cref="ViewName"/> view
/// whenever a step changes — never a diff. Labels and descriptions live in the schema's
/// declared flow (<see cref="FlowStepDescriptor"/>); observers join states onto it by id.
/// </summary>
public sealed record WorkflowStepFlow(IReadOnlyList<WorkflowStepState> Steps)
{
    public const string ViewName = "flow";
}

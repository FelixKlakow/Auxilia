namespace Auxilia.Workflows.Views;

/// <summary>One step of a workflow's declared flow; states are an open vocabulary
/// ("pending", "active", "done", "skipped" by convention).</summary>
public sealed record WorkflowStep(string Id, string Label, string State, string? Description = null);

/// <summary>
/// A FULL snapshot of the workflow's step flow, published on the <see cref="ViewName"/> view
/// whenever a step changes — observers render the latest snapshot as a clickable pipeline
/// (the run's "where am I"), never a diff.
/// </summary>
public sealed record WorkflowStepFlow(IReadOnlyList<WorkflowStep> Steps)
{
    public const string ViewName = "flow";
}

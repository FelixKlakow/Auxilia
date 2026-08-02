namespace Auxilia.Workflows.Views;

/// <summary>
/// One step of a workflow's DECLARED flow — the packaging-time data of the "flow" view
/// (<see cref="ViewDescriptor.DeclaredDataJson"/>), so dispatch/config UIs render the stage view
/// before any run exists. The optional <paramref name="SkipInput"/>/<paramref name="SkipValue"/>
/// pair is a data-driven skip hint — editors present the step as skipped when the effective value
/// of that run input equals the value; the platform never learns what either means.
/// <paramref name="Inputs"/> and <paramref name="Slots"/> name the run inputs and capability
/// slots belonging to this step: editors hide a skipped step's exclusive ones (a step's own
/// gating input always stays visible), and selecting a step filters the form down to what
/// that step owns.
/// </summary>
public sealed record FlowStepDescriptor(
    string Id,
    string Label,
    string? Description = null,
    string? SkipInput = null,
    string? SkipValue = null,
    IReadOnlyList<string>? Inputs = null,
    IReadOnlyList<string>? Slots = null);

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

    /// <summary>Renderer key of the stage view; its declared data is a <see cref="FlowStepDescriptor"/> list.</summary>
    public const string RendererKey = "step-flow";
}

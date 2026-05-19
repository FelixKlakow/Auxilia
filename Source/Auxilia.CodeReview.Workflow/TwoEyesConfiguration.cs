namespace Auxilia.CodeReview.Workflow;

public sealed record TwoEyesConfiguration
{
    public bool Enabled { get; init; }
    public string SecondarySlotName { get; init; } = "secondary-reviewer";
}

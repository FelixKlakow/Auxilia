namespace Auxilia.ImplementationWorkflow;

public sealed record ImplementationWorkflowConfiguration
{
    public WriteBackBehavior WriteBackBehavior { get; init; } = WriteBackBehavior.Warn;
    public bool ReviewerEnabled { get; init; } = false;
    public string OutputDirectory { get; init; } = "output";
}

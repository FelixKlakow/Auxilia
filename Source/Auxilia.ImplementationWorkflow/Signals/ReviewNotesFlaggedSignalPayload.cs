namespace Auxilia.ImplementationWorkflow.Signals;

public sealed record ReviewNoteDto(string Description, string? FilePath, string Severity);

public sealed record ReviewNotesFlaggedSignalPayload
{
    public required string PrUrl { get; init; }
    public required string BranchName { get; init; }
    public required string WorkItemId { get; init; }
    public required IReadOnlyList<ReviewNoteDto> ReviewNotes { get; init; }
}

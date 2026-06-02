namespace Auxilia.ImplementationWorkflow;

public sealed record ReviewNote(string Description, string? FilePath, ReviewNoteSeverity Severity);

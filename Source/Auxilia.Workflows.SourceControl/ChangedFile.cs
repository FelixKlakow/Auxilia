namespace Auxilia.Workflows.SourceControl;

/// <summary>Represents a file that was changed between two commit refs.</summary>
/// <param name="Path">Repository-relative file path.</param>
/// <param name="Kind">The type of change applied to this file.</param>
public record ChangedFile(string Path, ChangeKind Kind);

namespace Auxilia.Workflows.SourceControl;

public record ChangedFile(string RelativePath, ChangeKind Kind);

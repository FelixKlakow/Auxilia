namespace Auxilia.Workflows.PullRequestAccess;

public record ChangedFile(string RelativePath, ChangeKind Kind);

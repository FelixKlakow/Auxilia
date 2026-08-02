namespace Auxilia.Workflows.PullRequestAccess;

public record DiffHunk(string FilePath, int OldStart, int OldCount, int NewStart, int NewCount, string Content);

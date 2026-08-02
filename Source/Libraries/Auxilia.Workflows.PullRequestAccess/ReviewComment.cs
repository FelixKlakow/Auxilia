namespace Auxilia.Workflows.PullRequestAccess;

public record ReviewComment(string Id, string Body, string Author, string? FilePath, int? LineNumber, DateTimeOffset CreatedAt);

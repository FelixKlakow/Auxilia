namespace Auxilia.Slots.GitHub;

/// <summary>Typed view of the "repository" slot settings; owner/repo are parsed from the repository URL.</summary>
public sealed record GitHubRepositoryOptions
{
    public const string DefaultBranch = "main";

    public required string Owner { get; init; }

    public required string Repository { get; init; }

    /// <summary>Fine-grained personal access token; absent for public repositories and never logged.</summary>
    public string? Token { get; init; }

    public string Branch { get; init; } = DefaultBranch;

    /// <summary>Local path of the per-run clone when the workspace carries one; empty for API-only access.</summary>
    public string WorkingPath { get; init; } = "";

    public static GitHubRepositoryOptions FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        var url = settings.GetValueOrDefault("RepositoryUrl");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "The github-repository provider requires the RepositoryUrl setting.");

        var (owner, repository) = ParseOwnerAndRepository(url);
        return new GitHubRepositoryOptions
        {
            Owner = owner,
            Repository = repository,
            Token = settings.GetValueOrDefault("Token") is { Length: > 0 } token ? token : null,
            Branch = settings.GetValueOrDefault("Branch") is { Length: > 0 } branch ? branch : DefaultBranch
        };
    }

    private static (string Owner, string Repository) ParseOwnerAndRepository(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"RepositoryUrl is not a valid absolute URL: '{url}'.");

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new InvalidOperationException(
                $"RepositoryUrl must contain an owner and repository, e.g. https://github.com/acme/widget — got '{url}'.");

        var repository = segments[1];
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];
        if (segments[0].Length == 0 || repository.Length == 0)
            throw new InvalidOperationException(
                $"RepositoryUrl must contain an owner and repository, e.g. https://github.com/acme/widget — got '{url}'.");

        return (segments[0], repository);
    }
}

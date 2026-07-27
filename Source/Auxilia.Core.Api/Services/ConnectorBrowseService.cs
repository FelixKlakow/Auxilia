using System.Net.Http.Headers;
using System.Text.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Browses live data reachable with a connector's credential — repositories the account can
/// access, branches of one repository — WITHOUT the credential ever leaving the Core: settings
/// are decrypted here, the upstream API is called here, only names go back to the client.
/// GitHub and Azure DevOps / TFS (any connector carrying an <c>OrgUrl</c> setting — the
/// organization or collection URL) are implemented; anything else surfaces as not-supported so
/// clients fall back to manual entry.
/// </summary>
public sealed class ConnectorBrowseService(
    ConnectorTokenRefresher tokenRefresher,
    IHttpClientFactory httpClientFactory,
    ILogger<ConnectorBrowseService> logger)
{
    private static readonly string[] TokenKeys = ["Token", "token", "pat", "password", "accessToken"];

    public async Task<ConnectorBrowseResult> BrowseAsync(Guid connectorId, BrowseConnector request, CancellationToken ct)
    {
        // Refreshed-on-use: browsing with a rotated OAuth token would 401 pointlessly.
        var settings = await tokenRefresher.ResolveFreshSettingsAsync(connectorId, ct)
                       ?? throw new KeyNotFoundException();
        var token = TokenKeys.Select(key => settings.GetValueOrDefault(key))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? throw new NotSupportedException(
                "this connector holds no token setting — browsing needs a credential");

        // The host is derived from the connector's own settings: an OrgUrl (organization /
        // collection URL) marks Azure DevOps or on-prem TFS — both speak the same Git REST API;
        // anything else probes GitHub, and a failed probe falls back to manual entry.
        if (settings.GetValueOrDefault("OrgUrl") is { Length: > 0 } orgUrl)
            return await AzureDevOpsBrowseAsync(orgUrl.TrimEnd('/'), token, request, ct);

        return request.Kind switch
        {
            "repositories" => new ConnectorBrowseResult(await GitHubAsync(
                token, "https://api.github.com/user/repos?per_page=100&sort=pushed",
                repo => new ConnectorBrowseItem(
                    repo.GetProperty("clone_url").GetString() ?? "",
                    repo.GetProperty("full_name").GetString() ?? ""), ct)),
            "branches" when !string.IsNullOrWhiteSpace(request.Context)
                => new ConnectorBrowseResult(await GitHubAsync(
                    token, $"https://api.github.com/repos/{OwnerRepoOf(request.Context)}/branches?per_page=100",
                    branch => new ConnectorBrowseItem(
                        branch.GetProperty("name").GetString() ?? "",
                        branch.GetProperty("name").GetString() ?? ""), ct)),
            _ => throw new NotSupportedException($"unknown browse kind '{request.Kind}'"),
        };
    }

    // --- Azure DevOps / TFS (the same Git REST API serves dev.azure.com and on-prem servers) ---

    private async Task<ConnectorBrowseResult> AzureDevOpsBrowseAsync(
        string orgUrl, string pat, BrowseConnector request, CancellationToken ct)
    {
        switch (request.Kind)
        {
            case "repositories":
            {
                var repos = await AzureDevOpsGetAsync(
                    orgUrl, $"{orgUrl}/_apis/git/repositories?api-version=7.1", pat, ct);
                return new ConnectorBrowseResult(repos
                    .Select(repo => new ConnectorBrowseItem(
                        repo.GetProperty("remoteUrl").GetString() ?? "",
                        AzureDevOpsRepoLabel(repo)))
                    .Where(item => item.Id.Length > 0)
                    .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList());
            }
            case "branches" when !string.IsNullOrWhiteSpace(request.Context):
            {
                // The context is the CLONE URL the sibling setting holds — resolve it to the
                // repository's id (remote-URL match, name as the fallback), then list its heads.
                var repos = await AzureDevOpsGetAsync(
                    orgUrl, $"{orgUrl}/_apis/git/repositories?api-version=7.1", pat, ct);
                var repo = FindAzureDevOpsRepo(repos, request.Context)
                           ?? throw new NotSupportedException(
                               "the repository was not found in this organization — enter the branch manually");
                var projectId = repo.GetProperty("project").GetProperty("id").GetString();
                var repoId = repo.GetProperty("id").GetString();
                var refs = await AzureDevOpsGetAsync(
                    orgUrl,
                    $"{orgUrl}/{projectId}/_apis/git/repositories/{repoId}/refs?filter=heads/&api-version=7.1",
                    pat, ct);
                return new ConnectorBrowseResult(refs
                    .Select(r => (r.GetProperty("name").GetString() ?? "")
                        .Replace("refs/heads/", "", StringComparison.Ordinal))
                    .Where(name => name.Length > 0)
                    .Select(name => new ConnectorBrowseItem(name, name))
                    .ToList());
            }
            default:
                throw new NotSupportedException($"unknown browse kind '{request.Kind}'");
        }
    }

    /// <summary>"Project/Repo", collapsed to the repo name when they coincide (single-repo projects).</summary>
    internal static string AzureDevOpsRepoLabel(JsonElement repo)
    {
        var name = repo.GetProperty("name").GetString() ?? "";
        var project = repo.TryGetProperty("project", out var p)
                      && p.TryGetProperty("name", out var pn) ? pn.GetString() : null;
        return project is { Length: > 0 } && !string.Equals(project, name, StringComparison.OrdinalIgnoreCase)
            ? $"{project}/{name}"
            : name;
    }

    /// <summary>Matches a clone URL against the listed repositories (remote URL, then name).</summary>
    internal static JsonElement? FindAzureDevOpsRepo(IReadOnlyList<JsonElement> repos, string cloneUrl)
    {
        var normalized = NormalizeRepoUrl(cloneUrl);
        foreach (var repo in repos)
            if (NormalizeRepoUrl(repo.GetProperty("remoteUrl").GetString() ?? "") == normalized)
                return repo;
        var lastSegment = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        foreach (var repo in repos)
            if (string.Equals(repo.GetProperty("name").GetString(), lastSegment, StringComparison.OrdinalIgnoreCase))
                return repo;
        return null;
    }

    /// <summary>Lower-cased URL without scheme, userinfo, or a trailing ".git" — for matching.</summary>
    internal static string NormalizeRepoUrl(string url)
    {
        var value = url.Trim().TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
            value = value[(schemeEnd + 3)..];
        var at = value.IndexOf('@');
        if (at >= 0)
            value = value[(at + 1)..];
        return Uri.UnescapeDataString(value).ToLowerInvariant();
    }

    private async Task<List<JsonElement>> AzureDevOpsGetAsync(
        string orgUrl, string url, string pat, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // PAT auth: basic with an empty user name — identical for cloud and on-prem.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{pat}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Connector browse against {Url} answered {Status}.", url, response.StatusCode);
            throw new NotSupportedException(
                $"the credential could not browse {orgUrl} ({(int)response.StatusCode}) — enter the value manually");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("value", out var value)
            ? value.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
    }

    /// <summary>Accepts a clone URL or an owner/repo pair and yields "owner/repo".</summary>
    internal static string OwnerRepoOf(string repository)
    {
        var value = repository.Trim().TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : value;
    }

    private async Task<List<ConnectorBrowseItem>> GitHubAsync(
        string token, string url, Func<JsonElement, ConnectorBrowseItem> map, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Auxilia-Core", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Connector browse against {Url} answered {Status}.", url, response.StatusCode);
            throw new NotSupportedException(
                $"the credential could not browse GitHub ({(int)response.StatusCode}) — enter the value manually");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray().Select(map)
            .Where(item => item.Id.Length > 0)
            .ToList();
    }
}

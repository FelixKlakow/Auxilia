using System.Net.Http.Headers;
using System.Text.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Browses live data reachable with a connector's credential — repositories the account can
/// access, branches of one repository — WITHOUT the credential ever leaving the Core: settings
/// are decrypted here, the upstream API is called here, only names go back to the client.
/// GitHub is implemented; other hosts (TFS/Azure DevOps) surface as not-supported so clients
/// fall back to manual entry.
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

        // Only GitHub browsing is implemented; a non-GitHub token simply fails the probe and the
        // client falls back to manual entry.
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

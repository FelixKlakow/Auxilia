using System.Net.Http.Headers;
using System.Text.Json;
using Auxilia.Workflows.SourceControl;

namespace Auxilia.Slots.GitHub;

/// <summary>
/// Read-only <see cref="ISourceControlAccess"/> over the GitHub REST API v3. The optional
/// message handler is the test seam; the token reaches GitHub only as an Authorization
/// header and never appears in error messages or logs.
/// </summary>
public sealed class GitHubRepositoryAccess : ISourceControlAccess, IDisposable
{
    private readonly GitHubRepositoryOptions options;
    private readonly HttpClient httpClient;

    public GitHubRepositoryAccess(GitHubRepositoryOptions options, HttpMessageHandler? messageHandler = null)
    {
        this.options = options;
        httpClient = messageHandler is null ? new HttpClient() : new HttpClient(messageHandler);
        httpClient.BaseAddress = new Uri("https://api.github.com/");
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Auxilia", null));
        if (!string.IsNullOrEmpty(options.Token))
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
    }

    /// <summary>The per-run clone when the workspace carries one; empty for API-only access.</summary>
    public string WorkingPath => options.WorkingPath;

    public async Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            $"repos/{options.Owner}/{options.Repository}/git/trees/{Uri.EscapeDataString(options.Branch)}?recursive=1",
            cancellationToken);

        var prefix = relativePath?.Trim('/');
        var files = new List<string>();
        foreach (var entry in document.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "blob")
                continue;
            var path = entry.GetProperty("path").GetString()!;
            if (string.IsNullOrEmpty(prefix) || path.StartsWith(prefix + "/", StringComparison.Ordinal))
                files.Add(path);
        }

        return files;
    }

    public async Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var escapedPath = string.Join('/', relativePath.Trim('/').Split('/').Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"repos/{options.Owner}/{options.Repository}/contents/{escapedPath}?ref={Uri.EscapeDataString(options.Branch)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureSuccess(response);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            $"repos/{options.Owner}/{options.Repository}/compare/{Uri.EscapeDataString(baseRef)}...{Uri.EscapeDataString(headRef)}",
            cancellationToken);

        var changes = new List<ChangedFile>();
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = file.GetProperty("filename").GetString()!;
            var kind = file.GetProperty("status").GetString() switch
            {
                "added" => ChangeKind.Added,
                "removed" => ChangeKind.Deleted,
                "renamed" => ChangeKind.Renamed,
                _ => ChangeKind.Modified
            };
            changes.Add(new ChangedFile(path, kind));
        }

        return changes;
    }

    public void Dispose() => httpClient.Dispose();

    private async Task<JsonDocument> GetJsonAsync(string relativeUri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(relativeUri, cancellationToken);
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>Fails with status and path only — never with headers, so the token cannot leak.</summary>
    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub API request for {options.Owner}/{options.Repository} failed with status "
                + $"{(int)response.StatusCode} ({response.StatusCode}).");
    }
}

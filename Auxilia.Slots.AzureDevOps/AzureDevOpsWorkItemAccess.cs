using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.Slots.AzureDevOps;

/// <summary>
/// <see cref="IWorkItemAccess"/> over the Azure DevOps / TFS Work Item Tracking REST API —
/// cloud and on-prem speak the same API. The optional message handler is the test seam; the
/// PAT reaches the server only as the Basic Authorization header and never appears in error
/// messages or logs.
/// </summary>
public sealed class AzureDevOpsWorkItemAccess : IWorkItemAccess, IDisposable
{
    private readonly string orgUrl;
    private readonly HttpClient httpClient;
    private readonly Dictionary<string, CachedItem> cache = [];

    /// <summary>Project, work-item type and relations remembered per work item — comments, states, and links need them.</summary>
    private sealed record CachedItem(
        WorkItem Item,
        string Project,
        string WorkItemType,
        IReadOnlyList<(string Url, string FileName)> Attachments,
        IReadOnlyList<(string Rel, string Url, string? Comment)> Relations);

    public AzureDevOpsWorkItemAccess(string orgUrl, string personalAccessToken, HttpMessageHandler? messageHandler = null)
    {
        this.orgUrl = orgUrl.TrimEnd('/');
        httpClient = messageHandler is null ? new HttpClient() : new HttpClient(messageHandler);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + personalAccessToken)));
    }

    public async Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
        => (await ResolveAsync(id, cancellationToken))?.Item;

    public async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(
        IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var items = new List<WorkItem>();
        foreach (var id in ids)
        {
            if (await GetWorkItemAsync(id, cancellationToken) is { } item)
                items.Add(item);
        }

        return items;
    }

    public async Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
    {
        var cached = await ResolveAsync(id, cancellationToken)
                     ?? throw new InvalidOperationException($"Work item '{id}' was not found.");

        var url = $"{orgUrl}/{Uri.EscapeDataString(cached.Project)}/_apis/wit/workItems/{id}/comments"
                  + "?api-version=7.1-preview.3";
        using var response = await httpClient.PostAsync(
            url,
            new StringContent(JsonSerializer.Serialize(new { text = comment }), Encoding.UTF8, "application/json"),
            cancellationToken);
        await EnsureSuccessAsync(response, $"posting a comment on work item '{id}'", cancellationToken);
    }

    public async Task<IReadOnlyList<WorkItemAttachment>> GetAttachmentsAsync(
        string id, CancellationToken cancellationToken = default)
    {
        var cached = await ResolveAsync(id, cancellationToken);
        if (cached is null)
            return [];

        var attachments = new List<WorkItemAttachment>();
        foreach (var (url, fileName) in cached.Attachments)
        {
            // A single missing or unreadable attachment must never fail the whole read.
            try
            {
                var downloadUrl = url.Contains('?') ? url : url + "?api-version=7.0&download=true";
                using var response = await httpClient.GetAsync(downloadUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    continue;
                attachments.Add(new WorkItemAttachment(
                    fileName, await response.Content.ReadAsByteArrayAsync(cancellationToken)));
            }
            catch (HttpRequestException)
            {
            }
        }

        return attachments;
    }

    public async Task<IReadOnlyList<string>> GetStatesAsync(string id, CancellationToken cancellationToken = default)
    {
        var cached = await ResolveAsync(id, cancellationToken);
        if (cached is null)
            return [];

        var url = $"{orgUrl}/{Uri.EscapeDataString(cached.Project)}/_apis/wit/workitemtypes/"
                  + $"{Uri.EscapeDataString(cached.WorkItemType)}/states?api-version=7.1-preview.1";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, $"reading the states of work item '{id}'", cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return [.. document.RootElement.GetProperty("value").EnumerateArray()
            .Select(state => state.GetProperty("name").GetString() ?? "")
            .Where(name => name.Length > 0)];
    }

    public async Task SetStateAsync(string id, string state, CancellationToken cancellationToken = default)
    {
        var patch = JsonSerializer.Serialize(new[]
        {
            new { op = "add", path = "/fields/System.State", value = state },
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"{orgUrl}/_apis/wit/workitems/{id}?api-version=7.0")
        {
            Content = new StringContent(patch, Encoding.UTF8, "application/json-patch+json"),
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, $"setting work item '{id}' to state '{state}'", cancellationToken);
        cache.Remove(id);
    }

    public async Task<IReadOnlyList<WorkItemRelation>> GetRelationsAsync(
        string id, CancellationToken cancellationToken = default)
    {
        var cached = await ResolveAsync(id, cancellationToken);
        if (cached is null)
            return [];

        var relations = new List<WorkItemRelation>();
        foreach (var (rel, url, comment) in cached.Relations)
        {
            var kind = rel switch
            {
                "System.LinkTypes.Hierarchy-Reverse" => "parent",
                "System.LinkTypes.Hierarchy-Forward" => "child",
                "System.LinkTypes.Related" => "related",
                "System.LinkTypes.Dependency-Forward" => "successor",
                "System.LinkTypes.Dependency-Reverse" => "predecessor",
                "Hyperlink" => "link",
                "AttachedFile" => null, // attachments have their own read path
                _ => rel
            };
            if (kind is null)
                continue;
            if (kind == "link")
            {
                relations.Add(new WorkItemRelation(kind, "", comment, url));
                continue;
            }

            // Work-item relations end in /workItems/{id}; the title resolves best effort
            // (and lands in the cache for any later direct read).
            var targetId = url.Split('/').LastOrDefault() ?? "";
            string? title = null;
            if (targetId.Length > 0 && targetId.All(char.IsAsciiDigit))
                try
                {
                    title = (await GetWorkItemAsync(targetId, cancellationToken))?.Title;
                }
                catch (InvalidOperationException)
                {
                    // The link target may be unreadable with this PAT — the id still helps.
                }
            relations.Add(new WorkItemRelation(kind, targetId, title, url));
        }

        return relations;
    }

    public void Dispose() => httpClient.Dispose();

    private async Task<CachedItem?> ResolveAsync(string id, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;

        using var response = await httpClient.GetAsync(
            $"{orgUrl}/_apis/wit/workitems/{id}?api-version=7.0&$expand=relations", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, $"reading work item '{id}'", cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var fields = root.GetProperty("fields");

        var item = new WorkItem(
            id,
            Text(fields, "System.Title") ?? "",
            HtmlToPlainText(Text(fields, "System.Description")),
            Text(fields, "System.State"),
            AssigneeOf(fields),
            TagsOf(fields));

        var resolved = new CachedItem(
            item,
            Text(fields, "System.TeamProject") ?? "",
            Text(fields, "System.WorkItemType") ?? "",
            AttachmentRelationsOf(root),
            RelationsOf(root));
        cache[id] = resolved;
        return resolved;
    }

    private static IReadOnlyList<(string Rel, string Url, string? Comment)> RelationsOf(JsonElement root)
    {
        if (!root.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<(string, string, string?)>();
        foreach (var relation in relations.EnumerateArray())
        {
            var rel = relation.TryGetProperty("rel", out var r) ? r.GetString() : null;
            var url = relation.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (rel is not { Length: > 0 } || url is not { Length: > 0 })
                continue;
            var comment = relation.TryGetProperty("attributes", out var attributes)
                          && attributes.TryGetProperty("comment", out var c)
                ? c.GetString()
                : null;
            result.Add((rel, url, comment));
        }

        return result;
    }

    private static string? Text(JsonElement fields, string name)
        => fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? AssigneeOf(JsonElement fields)
    {
        if (!fields.TryGetProperty("System.AssignedTo", out var assignee))
            return null;
        return assignee.ValueKind switch
        {
            // Modern API: an identity object; very old TFS serializes a plain display string.
            JsonValueKind.Object when assignee.TryGetProperty("displayName", out var name) => name.GetString(),
            JsonValueKind.String => assignee.GetString(),
            _ => null,
        };
    }

    private static IReadOnlyList<string> TagsOf(JsonElement fields)
        => Text(fields, "System.Tags") is { Length: > 0 } tags
            ? [.. tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    private static IReadOnlyList<(string Url, string FileName)> AttachmentRelationsOf(JsonElement root)
    {
        if (!root.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
            return [];

        var attachments = new List<(string, string)>();
        foreach (var relation in relations.EnumerateArray())
        {
            if (relation.GetProperty("rel").GetString() != "AttachedFile")
                continue;
            var url = relation.GetProperty("url").GetString();
            if (string.IsNullOrEmpty(url))
                continue;
            var name = relation.TryGetProperty("attributes", out var attributes)
                       && attributes.TryGetProperty("name", out var fileName)
                ? fileName.GetString() ?? "attachment"
                : "attachment";
            attachments.Add((url, name));
        }

        return attachments;
    }

    /// <summary>Best-effort HTML → plain text: block-level breaks become newlines, tags are stripped, entities decoded.</summary>
    private static string? HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;
        var text = Regex.Replace(html, @"<\s*(br|/p|/div|/li|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        // The snippet aids diagnosis; the PAT never appears in it — it only ever travels as a header.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var snippet = body.Length > 300 ? body[..300] : body;
        throw new InvalidOperationException(
            $"Azure DevOps returned {(int)response.StatusCode} {response.StatusCode} while {operation}: {snippet}");
    }
}

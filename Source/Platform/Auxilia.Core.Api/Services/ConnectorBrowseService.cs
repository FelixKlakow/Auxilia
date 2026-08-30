using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Browses live data reachable with a connector's credential — repositories the account can
/// access, branches of one repository, the provider's model list — WITHOUT the credential ever
/// leaving the Core: settings are decrypted here, the upstream API is called here, only names go
/// back to the client. Every browse kind is DATA-DRIVEN: the provider's registration declares
/// the request, auth, and response mapping (<see cref="ProviderBrowseSpec"/> per kind,
/// <see cref="ProviderModelCatalog"/> for models) and the Core executes the spec without any
/// vendor knowledge — a provider without a spec for the requested kind surfaces as
/// not-supported so clients fall back to manual entry.
/// </summary>
public sealed class ConnectorBrowseService(
    ConnectorTokenRefresher tokenRefresher,
    ConnectorService connectors,
    ProviderCatalogService providerCatalog,
    IHttpClientFactory httpClientFactory,
    ILogger<ConnectorBrowseService> logger)
{
    private static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    public async Task<ConnectorBrowseResult> BrowseAsync(Guid connectorId, BrowseConnector request, CancellationToken ct)
    {
        // Refreshed-on-use: browsing with a rotated OAuth token would 401 pointlessly.
        var settings = await tokenRefresher.ResolveFreshSettingsAsync(connectorId, ct)
                       ?? throw new KeyNotFoundException();
        var providerType = (await connectors.GetAsync(connectorId, ct))?.ProviderType
            ?? throw new KeyNotFoundException();

        // Model listing keeps its own spec shape (ProviderModelCatalog) for auth fallback chains.
        if (request.Kind == "models")
        {
            var modelSpec = await providerCatalog.GetModelCatalogAsync(providerType, ct)
                ?? throw new NotSupportedException(
                    $"'{providerType}' declares no model catalog — enter the model manually");
            return await ExecuteModelCatalogAsync(modelSpec, settings, ct);
        }

        var spec = await providerCatalog.GetBrowseSpecAsync(providerType, request.Kind, ct)
            ?? throw new NotSupportedException(
                $"'{providerType}' declares no '{request.Kind}' browse — enter the value manually");
        return await ExecuteBrowseSpecAsync(spec, settings, request.Context, ct);
    }

    /// <summary>Executes a registered browse spec: resolve pre-step, templated GET, declared auth, declared response mapping.</summary>
    private async Task<ConnectorBrowseResult> ExecuteBrowseSpecAsync(
        ProviderBrowseSpec spec, IReadOnlyDictionary<string, string> settings, string? context, CancellationToken ct)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (spec.Resolve is { } resolve)
        {
            if (string.IsNullOrWhiteSpace(context))
                throw new NotSupportedException("this browse needs a context value — fill the depending setting first");
            var candidates = await GetItemsAsync(spec, resolve.UrlTemplate, resolve.ItemsPath, settings, context, resolved, ct);
            var match = FindByUrlThenName(candidates, resolve.MatchUrlField, resolve.MatchNameField, context)
                        ?? throw new NotSupportedException(
                            "no entry matching the current value was found — enter the value manually");
            foreach (var field in resolve.ExportFields)
                resolved[$"resolved.{field}"] = FieldOf(match, field) ?? "";
        }

        var items = await GetItemsAsync(spec, spec.UrlTemplate, spec.ItemsPath, settings, context, resolved, ct);
        var mapped = items.Select(item => Map(spec, item)).Where(item => item.Id.Length > 0);
        if (spec.SortByLabel)
            mapped = mapped.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase);
        return new ConnectorBrowseResult(mapped.ToList());
    }

    /// <summary>One templated, authenticated GET; returns the item array at the declared path (root when none).</summary>
    private async Task<List<JsonElement>> GetItemsAsync(
        ProviderBrowseSpec spec, string urlTemplate, string? itemsPath,
        IReadOnlyDictionary<string, string> settings, string? context,
        IReadOnlyDictionary<string, string> resolved, CancellationToken ct)
    {
        var url = Substitute(urlTemplate, settings, context, resolved);
        var http = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in spec.Headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);
        ApplyAuth(request, spec, settings);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Connector browse against {Url} answered {Status}.", url, response.StatusCode);
            throw new NotSupportedException(
                $"the credential could not browse ({(int)response.StatusCode}) — enter the value manually");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var array = string.IsNullOrEmpty(itemsPath) ? doc.RootElement : ElementAt(doc.RootElement, itemsPath);
        return array is { ValueKind: JsonValueKind.Array } itemArray
            ? itemArray.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
    }

    /// <summary>The declared auth as data: PAT basic (empty user name) wins over bearer; no credential = not-supported.</summary>
    private static void ApplyAuth(
        HttpRequestMessage request, ProviderBrowseSpec spec, IReadOnlyDictionary<string, string> settings)
    {
        var basic = spec.BasicPasswordSettingKey is { Length: > 0 } basicKey
            ? settings.GetValueOrDefault(basicKey) : null;
        var bearer = spec.BearerSettingKey is { Length: > 0 } bearerKey
            ? settings.GetValueOrDefault(bearerKey) : null;
        if (basic is { Length: > 0 })
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{basic}")));
        else if (bearer is { Length: > 0 })
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        else
            throw new NotSupportedException(
                "this connector holds no token setting — browsing needs a credential");
    }

    /// <summary>Fills the template's {placeholders} from the context, the resolve step, and the connector settings.</summary>
    internal static string Substitute(
        string template, IReadOnlyDictionary<string, string> settings,
        string? context, IReadOnlyDictionary<string, string> resolved)
        => Placeholder.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (name is "context" or "context:owner-repo")
            {
                if (string.IsNullOrWhiteSpace(context))
                    throw new NotSupportedException(
                        "this browse needs a context value — fill the depending setting first");
                return name == "context" ? context : OwnerRepoOf(context);
            }
            if (name.StartsWith("resolved.", StringComparison.Ordinal))
                return resolved.GetValueOrDefault(name)
                       ?? throw new NotSupportedException($"the browse spec resolves no '{name}' value");
            return settings.GetValueOrDefault(name) is { Length: > 0 } value
                ? value.TrimEnd('/')
                : throw new NotSupportedException(
                    $"this connector holds no '{name}' setting — browsing needs it");
        });

    /// <summary>Maps one response item through the spec's declared field paths.</summary>
    private static ConnectorBrowseItem Map(ProviderBrowseSpec spec, JsonElement item)
    {
        var id = FieldOf(item, spec.IdField) ?? "";
        if (spec.IdTrimPrefix is { Length: > 0 } trim)
            id = id.Replace(trim, "", StringComparison.Ordinal);
        var label = spec.LabelField is { Length: > 0 } labelField
            ? FieldOf(item, labelField) is { Length: > 0 } value ? value : id
            : id;
        // "Prefix/Label", collapsed when they coincide (e.g. single-repo projects).
        if (spec.LabelPrefixField is { Length: > 0 } prefixField
            && FieldOf(item, prefixField) is { Length: > 0 } prefix
            && !string.Equals(prefix, label, StringComparison.OrdinalIgnoreCase))
            label = $"{prefix}/{label}";
        return new ConnectorBrowseItem(id, label);
    }

    /// <summary>Matches the context against candidate items: normalized URL first, last path segment vs name as the fallback.</summary>
    internal static JsonElement? FindByUrlThenName(
        IReadOnlyList<JsonElement> items, string urlField, string nameField, string context)
    {
        var normalized = NormalizeRepoUrl(context);
        foreach (var item in items)
            if (NormalizeRepoUrl(FieldOf(item, urlField) ?? "") == normalized)
                return item;
        var lastSegment = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        foreach (var item in items)
            if (string.Equals(FieldOf(item, nameField), lastSegment, StringComparison.OrdinalIgnoreCase))
                return item;
        return null;
    }

    /// <summary>Dot-path traversal into nested objects; null when any segment is absent.</summary>
    internal static JsonElement? ElementAt(JsonElement element, string path)
    {
        var current = element;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current;
    }

    /// <summary>The string value at a dot path; null for missing or non-string values.</summary>
    internal static string? FieldOf(JsonElement element, string path)
        => ElementAt(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

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

    /// <summary>Accepts a clone URL or an owner/repo pair and yields "owner/repo".</summary>
    internal static string OwnerRepoOf(string repository)
    {
        var value = repository.Trim().TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : value;
    }

    /// <summary>Executes a provider's model-catalog spec: auth from the connector's settings, ids/labels from the declared response paths.</summary>
    private async Task<ConnectorBrowseResult> ExecuteModelCatalogAsync(
        ProviderModelCatalog spec, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, spec.Endpoint);
        foreach (var (name, value) in spec.Headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);

        var apiKey = spec.ApiKeySettingKey is { Length: > 0 } apiKeyKey
            ? settings.GetValueOrDefault(apiKeyKey) : null;
        var bearer = spec.BearerSettingKey is { Length: > 0 } bearerKey
            ? settings.GetValueOrDefault(bearerKey) : null;
        if (apiKey is { Length: > 0 } && spec.ApiKeyHeader is { Length: > 0 })
            request.Headers.TryAddWithoutValidation(spec.ApiKeyHeader, apiKey);
        else if (bearer is { Length: > 0 })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            foreach (var (name, value) in spec.BearerHeaders ?? new Dictionary<string, string>())
                request.Headers.TryAddWithoutValidation(name, value);
        }
        else
            throw new NotSupportedException(
                "this connector holds no credential the model catalog can authenticate with");

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "Model catalog against {Endpoint} answered {Status}.", spec.Endpoint, response.StatusCode);
            throw new NotSupportedException(
                $"the credential could not list models ({(int)response.StatusCode}) — enter the model manually");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var items = doc.RootElement.TryGetProperty(spec.ItemsPath, out var array)
                    && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];
        return new ConnectorBrowseResult(items
            .Select(item =>
            {
                var id = item.TryGetProperty(spec.IdField, out var idValue) ? idValue.GetString() ?? "" : "";
                var label = spec.LabelField is { Length: > 0 } labelField
                            && item.TryGetProperty(labelField, out var labelValue)
                    ? labelValue.GetString() ?? id : id;
                return new ConnectorBrowseItem(id, label);
            })
            .Where(item => item.Id.Length > 0)
            .ToList());
    }
}

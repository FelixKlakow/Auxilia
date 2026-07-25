using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Client;

/// <summary>HTTP implementation of <see cref="ICoreClient"/> over the Core REST API.</summary>
public sealed class CoreClient(HttpClient http) : ICoreClient
{
    // --- Run configurations ---

    public Task<RunConfiguration> CreateConfigurationAsync(CreateRunConfiguration request, CancellationToken ct = default)
        => PostAsync<CreateRunConfiguration, RunConfiguration>("/api/configurations", request, ct);

    public Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<RunConfiguration>($"/api/configurations/{id}", ct);

    public Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<RunConfiguration>>("/api/configurations?" + Query(
            ("workflowType", query.WorkflowType),
            ("enabled", query.Enabled?.ToString()),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public Task<RunAccepted> RunConfigurationAsync(Guid id, CancellationToken ct = default)
        => PostAsync<RunAccepted>($"/api/configurations/{id}/run", ct);

    // --- Runs ---

    public Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default)
        => PostAsync<RunRequest, RunAccepted>("/api/runs", request, ct);

    public Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<RunStatus>($"/api/runs/{id}", ct);

    public Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<RunStatus>>("/api/runs?" + Query(
            ("state", query.State),
            ("workflowType", query.WorkflowType),
            ("configurationId", query.ConfigurationId?.ToString()),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public Task CancelRunAsync(Guid id, CancellationToken ct = default)
        => PostAsync($"/api/runs/{id}/cancel", ct);

    // --- Connectors ---

    public Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default)
        => PostAsync<CreateConnector, Connector>("/api/connectors", request, ct);

    public Task<Connector?> GetConnectorAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<Connector>($"/api/connectors/{id}", ct);

    public Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<Connector>>("/api/connectors?" + Query(
            ("providerType", query.ProviderType),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public Task SetConnectorGrantsAsync(Guid id, SetConnectorGrants request, CancellationToken ct = default)
        => PostAsync($"/api/connectors/{id}/grants", request, ct);

    // --- Groups ---

    public Task<GroupDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default)
        => PostAsync<CreateGroupRequest, GroupDto>("/api/groups", request, ct);

    public async Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken ct = default)
        => await GetAsync<List<GroupDto>>("/api/groups", ct);

    public Task AddGroupMemberAsync(Guid groupId, AddGroupMemberRequest request, CancellationToken ct = default)
        => PostAsync($"/api/groups/{groupId}/members", request, ct);

    public Task AssignGroupRoleAsync(Guid groupId, AssignGroupRoleRequest request, CancellationToken ct = default)
        => PostAsync($"/api/groups/{groupId}/roles", request, ct);

    // --- Directory group → role mappings ---

    public async Task<IReadOnlyList<GroupMappingDto>> ListGroupMappingsAsync(CancellationToken ct = default)
        => await GetAsync<List<GroupMappingDto>>("/api/identity/group-mappings", ct);

    public Task<GroupMappingDto> CreateGroupMappingAsync(CreateGroupMappingRequest request, CancellationToken ct = default)
        => PostAsync<CreateGroupMappingRequest, GroupMappingDto>("/api/identity/group-mappings", request, ct);

    public Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default)
        => DeleteAsync($"/api/identity/group-mappings/{id}", ct);

    // --- Identity / diagnostics ---

    public Task<CurrentPrincipal> GetCurrentPrincipalAsync(CancellationToken ct = default)
        => GetAsync<CurrentPrincipal>("/auth/me", ct);

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/health", ct);
        return response.IsSuccessStatusCode;
    }

    // --- Transport helpers (every non-success surfaces as CoreApiException) ---

    private async Task<TResult> PostAsync<TRequest, TResult>(string url, TRequest body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, body, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TResult>(ct))!;
    }

    private async Task<TResult> PostAsync<TResult>(string url, CancellationToken ct)
    {
        using var response = await http.PostAsync(url, null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TResult>(ct))!;
    }

    private async Task PostAsync<TRequest>(string url, TRequest body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, body, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task PostAsync(string url, CancellationToken ct)
    {
        using var response = await http.PostAsync(url, null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<TResult> GetAsync<TResult>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TResult>(ct))!;
    }

    private async Task<TResult?> GetOrNullAsync<TResult>(string url, CancellationToken ct) where TResult : class
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TResult>(ct);
    }

    private async Task DeleteAsync(string url, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? detail = null;
        try { detail = (await response.Content.ReadFromJsonAsync<ErrorBody>(ct))?.Error; }
        catch { /* the body was not the standard { error } shape */ }
        throw new CoreApiException(response.StatusCode, detail,
            $"Core API request failed ({(int)response.StatusCode} {response.StatusCode})"
            + (detail is null ? "" : $": {detail}"));
    }

    private static string Query(params (string Key, string? Value)[] parts)
        => string.Join("&", parts
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));

    private sealed record ErrorBody(string? Error);
}

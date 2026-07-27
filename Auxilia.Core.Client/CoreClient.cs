using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Client;

/// <summary>HTTP implementation of <see cref="ICoreClient"/> over the Core REST API.</summary>
public sealed class CoreClient(HttpClient http) : ICoreClient
{
    // --- Run configurations ---

    public Task<RunConfiguration> CreateConfigurationAsync(CreateRunConfiguration request, CancellationToken ct = default)
        => PostAsync<CreateRunConfiguration, RunConfiguration>("/api/configurations", request, ct);

    public Task<RunConfiguration> UpdateConfigurationAsync(
        Guid id, UpdateRunConfiguration request, CancellationToken ct = default)
        => PutAsync<UpdateRunConfiguration, RunConfiguration>($"/api/configurations/{id}", request, ct);

    public Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<RunConfiguration>($"/api/configurations/{id}", ct);

    public Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<RunConfiguration>>("/api/configurations?" + Query(
            ("workflowType", query.WorkflowType),
            ("enabled", query.Enabled?.ToString()),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public Task DeleteConfigurationAsync(Guid id, CancellationToken ct = default)
        => DeleteAsync($"/api/configurations/{id}", ct);

    public Task<RunAccepted> RunConfigurationAsync(
        Guid id, Guid? onBehalfOf = null, IReadOnlyDictionary<string, string>? context = null,
        CancellationToken ct = default)
        => PostAsync<IReadOnlyDictionary<string, string>?, RunAccepted>(
            $"/api/configurations/{id}/run" + (onBehalfOf is { } target ? $"?onBehalfOf={target}" : ""),
            context, ct);

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

    public Task<RunAccepted> RerunAsync(Guid id, CancellationToken ct = default)
        => PostAsync<RunAccepted>($"/api/runs/{id}/rerun", ct);

    public async IAsyncEnumerable<RunStreamEvent> StreamRunAsync(
        Guid runId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{runId}/stream");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            var evt = JsonSerializer.Deserialize<RunStreamEvent>(line["data: ".Length..], JsonSerializerOptions.Web);
            if (evt is not null)
                yield return evt;
        }
    }

    public Task<PagedResult<RunViewItem>> GetRunViewsAsync(
        Guid runId, string? view = null, int skip = 0, int take = 200, CancellationToken ct = default)
        => GetAsync<PagedResult<RunViewItem>>($"/api/runs/{runId}/views?" + Query(
            ("view", view),
            ("skip", skip.ToString()),
            ("take", take.ToString())), ct);

    public Task ProvideInputAsync(Guid runId, string payloadJson, CancellationToken ct = default)
        => PostAsync($"/api/runs/{runId}/inputs", new ProvideRunInput(payloadJson), ct);

    public async Task<int> ClearFinishedRunsAsync(CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync("/api/runs", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ClearedRuns>(ct))?.Deleted ?? 0;
    }

    private sealed record ClearedRuns(int Deleted);

    // --- Audit ---

    public Task<PagedResult<AuditEntry>> QueryAuditAsync(AuditQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<AuditEntry>>("/api/audit?" + Query(
            ("actor", query.Actor),
            ("action", query.Action),
            ("subject", query.Subject),
            ("fromUtc", query.FromUtc?.ToString("O")),
            ("toUtc", query.ToUtc?.ToString("O")),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    // --- Connectors ---

    public Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default)
        => PostAsync<CreateConnector, Connector>("/api/connectors", request, ct);

    public Task<Connector> UpdateConnectorAsync(Guid id, UpdateConnector request, CancellationToken ct = default)
        => PutAsync<UpdateConnector, Connector>($"/api/connectors/{id}", request, ct);

    public Task<Connector?> GetConnectorAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<Connector>($"/api/connectors/{id}", ct);

    public Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<Connector>>("/api/connectors?" + Query(
            ("providerType", query.ProviderType),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public Task SetConnectorGrantsAsync(Guid id, SetConnectorGrants request, CancellationToken ct = default)
        => PostAsync($"/api/connectors/{id}/grants", request, ct);

    public Task DeleteConnectorAsync(Guid id, CancellationToken ct = default)
        => DeleteAsync($"/api/connectors/{id}", ct);

    public Task<ConnectorBrowseResult> BrowseConnectorAsync(Guid id, BrowseConnector request, CancellationToken ct = default)
        => PostAsync<BrowseConnector, ConnectorBrowseResult>($"/api/connectors/{id}/browse", request, ct);

    // --- Provider catalog ---

    public Task<PagedResult<ProviderCatalogEntry>> QueryProviderCatalogAsync(ProviderCatalogQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<ProviderCatalogEntry>>("/api/provider-catalog?" + Query(
            ("available", query.Available?.ToString()),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public Task<ProviderCatalogEntry> RegisterProviderAsync(RegisterSlotProvider request, CancellationToken ct = default)
        => PostAsync<RegisterSlotProvider, ProviderCatalogEntry>("/api/provider-catalog", request, ct);

    public Task DeleteProviderAsync(string providerType, CancellationToken ct = default)
        => DeleteAsync($"/api/provider-catalog/{Uri.EscapeDataString(providerType)}", ct);

    public Task<ProviderCatalogEntry> SetProviderAvailabilityAsync(string providerType, bool available, CancellationToken ct = default)
        => PostAsync<SetProviderAvailability, ProviderCatalogEntry>(
            $"/api/provider-catalog/{Uri.EscapeDataString(providerType)}/availability",
            new SetProviderAvailability(available), ct);

    public Task<ProviderCatalogEntry> SetProviderSettingDisabledAsync(
        string providerType, string settingKey, bool disabled, CancellationToken ct = default)
        => PostAsync<SetProviderSetting, ProviderCatalogEntry>(
            $"/api/provider-catalog/{Uri.EscapeDataString(providerType)}/settings",
            new SetProviderSetting(settingKey, disabled), ct);

    // --- Environment layers ---

    public Task<IReadOnlyList<EnvironmentLayerDto>> ListEnvironmentLayersAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<EnvironmentLayerDto>>("/api/environment-layers", ct);

    public Task<EnvironmentLayerDto?> GetEnvironmentLayerAsync(string providerType, CancellationToken ct = default)
        => GetOrNullAsync<EnvironmentLayerDto>(
            $"/api/environment-layers/{Uri.EscapeDataString(providerType)}", ct);

    public Task<EnvironmentLayerDto> UpsertEnvironmentLayerAsync(
        UpsertEnvironmentLayer request, CancellationToken ct = default)
        => PostAsync<UpsertEnvironmentLayer, EnvironmentLayerDto>("/api/environment-layers", request, ct);

    public Task DeleteEnvironmentLayerAsync(string providerType, CancellationToken ct = default)
        => DeleteAsync($"/api/environment-layers/{Uri.EscapeDataString(providerType)}", ct);

    // --- Workflow types + schemas ---

    public Task<PagedResult<WorkflowTypeDto>> ListWorkflowTypesAsync(WorkflowTypeQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<WorkflowTypeDto>>("/api/workflow-types?" + Query(
            ("status", query.Status),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public Task<WorkflowSchemaDto?> GetWorkflowSchemaAsync(string workflowType, CancellationToken ct = default)
        => GetOrNullAsync<WorkflowSchemaDto>(
            $"/api/workflow-types/{Uri.EscapeDataString(workflowType)}/schema", ct);

    public Task<WorkflowTypeRegistrationDto> RegisterWorkflowTypeAsync(
        RegisterWorkflowTypeRequest request, CancellationToken ct = default)
        => PostAsync<RegisterWorkflowTypeRequest, WorkflowTypeRegistrationDto>("/api/workflow-types", request, ct);

    public Task<WorkflowTypeRegistrationDto?> GetWorkflowTypeRegistrationAsync(
        string workflowType, CancellationToken ct = default)
        => GetOrNullAsync<WorkflowTypeRegistrationDto>(
            $"/api/workflow-types/{Uri.EscapeDataString(workflowType)}/registration", ct);

    public Task UnregisterWorkflowTypeAsync(string workflowType, CancellationToken ct = default)
        => DeleteAsync($"/api/workflow-types/{Uri.EscapeDataString(workflowType)}", ct);

    public Task<WorkflowTypeRegistrationDto> ApproveWorkflowTypeAsync(string workflowType, CancellationToken ct = default)
        => PostAsync<WorkflowTypeRegistrationDto>(
            $"/api/workflow-types/{Uri.EscapeDataString(workflowType)}/approve", ct);

    public Task<WorkflowTypeRegistrationDto> DenyWorkflowTypeAsync(
        string workflowType, string reason, CancellationToken ct = default)
        => PostAsync<DenyWorkflowTypeRequest, WorkflowTypeRegistrationDto>(
            $"/api/workflow-types/{Uri.EscapeDataString(workflowType)}/deny",
            new DenyWorkflowTypeRequest(reason), ct);

    // --- Groups ---

    public Task<GroupDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default)
        => PostAsync<CreateGroupRequest, GroupDto>("/api/groups", request, ct);

    public async Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken ct = default)
        => await GetAsync<List<GroupDto>>("/api/groups", ct);

    public Task AddGroupMemberAsync(Guid groupId, AddGroupMemberRequest request, CancellationToken ct = default)
        => PostAsync($"/api/groups/{groupId}/members", request, ct);

    public Task AssignGroupRoleAsync(Guid groupId, AssignGroupRoleRequest request, CancellationToken ct = default)
        => PostAsync($"/api/groups/{groupId}/roles", request, ct);

    // --- Principals ---

    public Task<PagedResult<PrincipalDto>> QueryPrincipalsAsync(PrincipalQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<PrincipalDto>>("/api/principals?" + Query(
            ("kind", query.Kind),
            ("enabled", query.Enabled?.ToString()),
            ("search", query.Search),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public Task<PrincipalDto?> GetPrincipalAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<PrincipalDto>($"/api/principals/{id}", ct);

    public Task<PrincipalDto> CreateHumanPrincipalAsync(CreateHumanPrincipalRequest request, CancellationToken ct = default)
        => PostAsync<CreateHumanPrincipalRequest, PrincipalDto>("/api/principals", request, ct);

    public Task<CreatedApiKeyPrincipal> CreateApiKeyPrincipalAsync(CreateApiKeyPrincipalRequest request, CancellationToken ct = default)
        => PostAsync<CreateApiKeyPrincipalRequest, CreatedApiKeyPrincipal>("/api/principals/ai", request, ct);

    public Task AssignPrincipalRoleAsync(Guid id, AssignRoleRequest request, CancellationToken ct = default)
        => PostAsync($"/api/principals/{id}/roles", request, ct);

    public Task RevokePrincipalRoleAsync(Guid id, string roleName, CancellationToken ct = default)
        => DeleteAsync($"/api/principals/{id}/roles/{Uri.EscapeDataString(roleName)}", ct);

    public Task SetPrincipalEnabledAsync(Guid id, SetPrincipalEnabledRequest request, CancellationToken ct = default)
        => PostAsync($"/api/principals/{id}/enabled", request, ct);

    // --- Directory group → role mappings ---

    public async Task<IReadOnlyList<GroupMappingDto>> ListGroupMappingsAsync(CancellationToken ct = default)
        => await GetAsync<List<GroupMappingDto>>("/api/identity/group-mappings", ct);

    public Task<GroupMappingDto> CreateGroupMappingAsync(CreateGroupMappingRequest request, CancellationToken ct = default)
        => PostAsync<CreateGroupMappingRequest, GroupMappingDto>("/api/identity/group-mappings", request, ct);

    public Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default)
        => DeleteAsync($"/api/identity/group-mappings/{id}", ct);

    // --- Identity sources ---

    public async Task<IReadOnlyList<IdentityConnectorDescriptorDto>> ListIdentityConnectorsAsync(CancellationToken ct = default)
        => await GetAsync<List<IdentityConnectorDescriptorDto>>("/api/identity/connectors", ct);

    public async Task<IReadOnlyList<IdentitySourceDto>> ListIdentitySourcesAsync(CancellationToken ct = default)
        => await GetAsync<List<IdentitySourceDto>>("/api/identity/sources", ct);

    public Task<IdentitySourceDto?> GetIdentitySourceAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<IdentitySourceDto>($"/api/identity/sources/{id}", ct);

    public Task<IdentitySourceDto> SaveIdentitySourceAsync(SaveIdentitySourceRequest request, CancellationToken ct = default)
        => PostAsync<SaveIdentitySourceRequest, IdentitySourceDto>("/api/identity/sources", request, ct);

    public Task DeleteIdentitySourceAsync(Guid id, CancellationToken ct = default)
        => DeleteAsync($"/api/identity/sources/{id}", ct);

    public Task<IdentityConnectorTestResult> TestIdentitySourceAsync(Guid id, CancellationToken ct = default)
        => PostAsync<IdentityConnectorTestResult>($"/api/identity/sources/{id}/test", ct);

    public Task<IdentityImportSummaryDto> ImportIdentitySourceAsync(Guid id, CancellationToken ct = default)
        => PostAsync<IdentityImportSummaryDto>($"/api/identity/sources/{id}/import", ct);

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

    private async Task<TResult> PutAsync<TRequest, TResult>(string url, TRequest body, CancellationToken ct)
    {
        using var response = await http.PutAsJsonAsync(url, body, ct);
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

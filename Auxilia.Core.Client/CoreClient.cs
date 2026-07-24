using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Client;

/// <summary>HTTP implementation of <see cref="ICoreClient"/> over the Core REST API.</summary>
public sealed class CoreClient(HttpClient http) : ICoreClient
{
    public Task<RunConfiguration> CreateConfigurationAsync(CreateRunConfiguration request, CancellationToken ct = default)
        => PostAsync<CreateRunConfiguration, RunConfiguration>("/api/configurations", request, ct);

    public Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<RunConfiguration>($"/api/configurations/{id}", ct);

    public async Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default)
        => (await http.GetFromJsonAsync<PagedResult<RunConfiguration>>(
            "/api/configurations?" + Query(
                ("workflowType", query.WorkflowType),
                ("enabled", query.Enabled?.ToString()),
                ("skip", query.Skip.ToString()),
                ("take", query.Take.ToString())), ct))!;

    public async Task<RunAccepted> RunConfigurationAsync(Guid id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"/api/configurations/{id}/run", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RunAccepted>(ct))!;
    }

    public Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default)
        => PostAsync<RunRequest, RunAccepted>("/api/runs", request, ct);

    public Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<RunStatus>($"/api/runs/{id}", ct);

    public async Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default)
        => (await http.GetFromJsonAsync<PagedResult<RunStatus>>(
            "/api/runs?" + Query(
                ("state", query.State),
                ("workflowType", query.WorkflowType),
                ("configurationId", query.ConfigurationId?.ToString()),
                ("skip", query.Skip.ToString()),
                ("take", query.Take.ToString())), ct))!;

    public async Task CancelRunAsync(Guid id, CancellationToken ct = default)
        => (await http.PostAsync($"/api/runs/{id}/cancel", null, ct)).EnsureSuccessStatusCode();

    public Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default)
        => PostAsync<CreateConnector, Connector>("/api/connectors", request, ct);

    public async Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default)
        => (await http.GetFromJsonAsync<PagedResult<Connector>>(
            "/api/connectors?" + Query(
                ("providerType", query.ProviderType),
                ("skip", query.Skip.ToString()),
                ("take", query.Take.ToString())), ct))!;

    private async Task<TResult> PostAsync<TRequest, TResult>(string url, TRequest body, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(url, body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResult>(ct))!;
    }

    private async Task<TResult?> GetOrNullAsync<TResult>(string url, CancellationToken ct) where TResult : class
    {
        var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResult>(ct);
    }

    private static string Query(params (string Key, string? Value)[] parts)
        => string.Join("&", parts
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
}

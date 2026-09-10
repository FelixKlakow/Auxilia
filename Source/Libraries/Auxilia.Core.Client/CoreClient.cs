using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Client;

/// <summary>
/// HTTP implementation of <see cref="ICoreClient"/> over the Core REST API. Streams reconnect
/// internally (backoff + idle detection) and yield <see cref="ClientStreamFrame{TEvent}"/>s;
/// unary calls are bounded by <see cref="CoreClientOptions.UnaryTimeoutSeconds"/> because the
/// underlying <see cref="HttpClient.Timeout"/> is disabled (it would sever long-lived streams).
/// </summary>
public sealed class CoreClient : ICoreClient
{
    private readonly HttpClient http;
    private readonly CoreClientOptions options;

    public CoreClient(HttpClient http, CoreClientOptions? options = null)
    {
        this.http = http;
        this.options = options ?? new CoreClientOptions();
        try
        {
            // SSE streams must outlive the 100s HttpClient default timeout; per-call unary
            // timeouts (linked CTS in the transport helpers) take over the watchdog role.
            http.Timeout = Timeout.InfiniteTimeSpan;
        }
        catch (InvalidOperationException)
        {
            // The HttpClient was already used (shared test client) — its timeout stands.
        }
    }

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

    public Task<RunConfiguration> SetConfigurationGrantsAsync(
        Guid id, SetConfigurationGrants request, CancellationToken ct = default)
        => PutAsync<SetConfigurationGrants, RunConfiguration>($"/api/configurations/{id}/grants", request, ct);

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

    public IAsyncEnumerable<ClientStreamFrame<RunStreamEvent>> StreamRunAsync(
        Guid runId, CancellationToken ct = default)
        => StreamResilientAsync<RunStreamEvent>(
            () => NewSseRequest($"/api/runs/{runId}/stream"),
            isTerminal: evt => evt.Kind == RunStreamEvent.StatusKind && RunStates.IsTerminal(StatusStateOf(evt)),
            // Sequence is per-(run, VIEW) monotonic, so the dedupe key must carry the view name —
            // a single high-water mark would drop live frames of whichever view's counter lags.
            dedupeOf: evt => evt.Kind == RunStreamEvent.ViewKind
                ? (evt.AsView()?.ViewName ?? "", evt.Sequence)
                : null,
            ct);

    public Task<PagedResult<RunViewItem>> GetRunViewsAsync(
        Guid runId, string? view = null, int skip = 0, int take = 200, CancellationToken ct = default)
        => GetAsync<PagedResult<RunViewItem>>($"/api/runs/{runId}/views?" + Query(
            ("view", view),
            ("skip", skip.ToString()),
            ("take", take.ToString())), ct);

    public Task<RunStats> GetRunStatsAsync(CancellationToken ct = default)
        => GetAsync<RunStats>("/api/runs/stats", ct);

    public Task<IReadOnlyList<DashboardPin>> ListDashboardPinsAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<DashboardPin>>("/api/dashboard/pins", ct);

    public Task<DashboardPin> PinDashboardViewAsync(CreateDashboardPin request, CancellationToken ct = default)
        => PostAsync<CreateDashboardPin, DashboardPin>("/api/dashboard/pins", request, ct);

    public Task UnpinDashboardViewAsync(Guid pinId, CancellationToken ct = default)
        => DeleteAsync($"/api/dashboard/pins/{pinId}", ct);

    public Task ProvideInputAsync(Guid runId, string payloadJson, CancellationToken ct = default)
        => PostAsync($"/api/runs/{runId}/inputs", new ProvideRunInput(payloadJson), ct);

    public async Task<TerminalTicket> OpenTerminalAsync(Guid runId, CancellationToken ct = default)
    {
        var ticket = await PostAsync<TerminalTicket>($"/api/runs/{runId}/terminal-ticket", ct);
        // The Core hands out a relative URL; resolve it against this client's base address so
        // callers can open it directly (browser, WebView) without knowing the Core's address.
        return http.BaseAddress is { } baseAddress
            ? ticket with { Url = new Uri(baseAddress, ticket.Url).ToString() }
            : ticket;
    }

    public Task<int> ClearFinishedRunsAsync(CancellationToken ct = default)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.DeleteAsync("/api/runs", token);
            await EnsureSuccessAsync(response, token);
            return (await response.Content.ReadFromJsonAsync<ClearedRuns>(token))?.Deleted ?? 0;
        });

    private sealed record ClearedRuns(int Deleted);

    // --- Artifacts ---

    public Task<PagedResult<ArtifactDto>> QueryArtifactsAsync(ArtifactQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<ArtifactDto>>("/api/artifacts?" + Query(
            ("artifactType", query.ArtifactType),
            ("workItemId", query.WorkItemId),
            ("runId", query.RunId?.ToString()),
            ("createdAfterUtc", query.CreatedAfterUtc?.ToString("O")),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public Task<ArtifactDto?> GetArtifactAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<ArtifactDto>($"/api/artifacts/{id}", ct);

    public async Task<Stream?> OpenArtifactContentAsync(Guid id, CancellationToken ct = default)
    {
        var response = await http.GetAsync(
            $"/api/artifacts/{id}/content", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public IAsyncEnumerable<ClientStreamFrame<ArtifactStreamEvent>> StreamArtifactEventsAsync(
        string? artifactType = null, string? workItemId = null, CancellationToken ct = default)
        => StreamResilientAsync<ArtifactStreamEvent>(
            () => NewSseRequest("/api/artifacts/stream?" + Query(
                ("artifactType", artifactType),
                ("workItemId", workItemId))),
            isTerminal: _ => false,
            dedupeOf: _ => null,
            ct);

    // --- Events ---

    public Task<PagedResult<EventDto>> QueryEventsAsync(EventQuery query, CancellationToken ct = default)
        => GetAsync<PagedResult<EventDto>>("/api/events?" + Query(
            ("eventType", query.EventType),
            ("workItemId", query.WorkItemId),
            ("sourceRunId", query.SourceRunId?.ToString()),
            ("createdAfterUtc", query.CreatedAfterUtc?.ToString("O")),
            ("createdBeforeUtc", query.CreatedBeforeUtc?.ToString("O")),
            ("skip", query.Skip.ToString()),
            ("take", query.Take.ToString())), ct);

    public IAsyncEnumerable<ClientStreamFrame<EventStreamEvent>> StreamEventsAsync(
        string? eventType = null, string? workItemId = null, CancellationToken ct = default)
        => StreamResilientAsync<EventStreamEvent>(
            () => NewSseRequest("/api/events/stream?" + Query(
                ("eventType", eventType),
                ("workItemId", workItemId))),
            isTerminal: _ => false,
            dedupeOf: _ => null,
            ct);

    // --- Resilient SSE core (reconnect + idle detection live HERE, never in callers) ---

    private static HttpRequestMessage NewSseRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    /// <summary>
    /// Opens the SSE endpoint and reconnects with exponential backoff on any drop — an orderly
    /// close without a prior terminal event IS a drop (the Core recycled). A terminal event ends
    /// the enumeration for good. Auth/not-found errors (non-transient) rethrow; every other
    /// failure surfaces as a <see cref="StreamConnectionFrame{TEvent}"/> and is retried. Events
    /// carrying a dedupe key are deduped per key across resubscribes (the server snapshot
    /// re-delivers) — sequences are only monotonic within a key (e.g. per view name), never across
    /// keys. Keepalive comments reset the idle watchdog; silence beyond
    /// <see cref="CoreClientOptions.StreamIdleTimeoutSeconds"/> counts as a drop — during the
    /// connect phase too: an accepted connection that never sends response headers would otherwise
    /// hang <c>SendAsync</c> forever (observed against a live Core).
    /// </summary>
    private async IAsyncEnumerable<ClientStreamFrame<TEvent>> StreamResilientAsync<TEvent>(
        Func<HttpRequestMessage> requestFactory,
        Func<TEvent, bool> isTerminal,
        Func<TEvent, (string Key, long Sequence)?> dedupeOf,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var attempt = 0;
        var initialBackoff = TimeSpan.FromSeconds(Math.Max(0, options.StreamReconnectInitialBackoffSeconds));
        var maxBackoff = TimeSpan.FromSeconds(Math.Max(1, options.StreamReconnectMaxBackoffSeconds));
        var idleTimeout = TimeSpan.FromSeconds(options.StreamIdleTimeoutSeconds);
        var backoff = initialBackoff;
        // Survives reconnects on purpose: the server re-delivers a snapshot on every resubscribe.
        var maxSeenSequences = new Dictionary<string, long>(StringComparer.Ordinal);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            HttpResponseMessage? response = null;
            Exception? failure = null;
            try
            {
                using var request = requestFactory();
                // HttpClient.Timeout is disabled, so the connect phase needs its own watchdog:
                // reuse the idle timeout — headers that never arrive are just pre-body silence.
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (idleTimeout > TimeSpan.Zero)
                    connectCts.CancelAfter(idleTimeout);
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
                await EnsureSuccessAsync(response, connectCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                response?.Dispose();
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Only the connect watchdog can cancel here (the caller's ct is checked above) —
                // treat it like any other transient drop and retry.
                failure = new TimeoutException(
                    $"The SSE connect produced no response headers within {idleTimeout.TotalSeconds:0}s — treating the connection as dead.", ex);
                response?.Dispose();
                response = null;
            }
            catch (CoreApiException ex) when (!IsTransient(ex.StatusCode))
            {
                // 401/403/404: retrying cannot help — the caller must handle it.
                response?.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or CoreApiException)
            {
                failure = ex;
                response?.Dispose();
                response = null;
            }

            if (response is not null)
            {
                var terminal = false;
                using (response)
                {
                    yield return new StreamConnectionFrame<TEvent>(StreamConnectionState.Connected, attempt);

                    Stream? body = null;
                    try
                    {
                        body = await response.Content.ReadAsStreamAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is HttpRequestException or IOException)
                    {
                        failure = ex;
                    }

                    if (body is not null)
                    {
                        using var reader = new StreamReader(body);
                        while (true)
                        {
                            string? line;
                            try
                            {
                                line = await ReadLineWithIdleTimeoutAsync(reader, idleTimeout, ct);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception ex)
                            {
                                failure = ex;
                                break;
                            }
                            if (line is null)
                                break; // orderly close — without a terminal event this is a drop
                            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                                continue; // ": ping" keepalives and blank lines reset the watchdog

                            TEvent? evt;
                            try
                            {
                                evt = JsonSerializer.Deserialize<TEvent>(
                                    line["data: ".Length..], JsonSerializerOptions.Web);
                            }
                            catch (JsonException)
                            {
                                continue;
                            }
                            if (evt is null)
                                continue;

                            if (dedupeOf(evt) is { } dedupe)
                            {
                                if (maxSeenSequences.TryGetValue(dedupe.Key, out var maxSeen)
                                    && dedupe.Sequence <= maxSeen)
                                    continue; // resubscribe overlap — already delivered
                                maxSeenSequences[dedupe.Key] = dedupe.Sequence;
                            }

                            backoff = initialBackoff; // a live event proves the link — reset
                            yield return new StreamEventFrame<TEvent>(evt);
                            if (isTerminal(evt))
                            {
                                terminal = true;
                                break;
                            }
                        }
                    }
                }
                if (terminal)
                    yield break;
            }

            yield return new StreamConnectionFrame<TEvent>(
                StreamConnectionState.Reconnecting, attempt, backoff, failure);
            if (backoff > TimeSpan.Zero)
                await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromTicks(Math.Min(maxBackoff.Ticks, Math.Max(1, backoff.Ticks) * 2));
        }
    }

    private static async Task<string?> ReadLineWithIdleTimeoutAsync(
        StreamReader reader, TimeSpan idleTimeout, CancellationToken ct)
    {
        if (idleTimeout <= TimeSpan.Zero)
            return await reader.ReadLineAsync(ct);
        // CANCEL the read on idle timeout — never abandon it: disposing the response with a
        // read still in flight can wedge the connection teardown, and the NEXT subscribe then
        // hangs in SendAsync forever (observed against a live Core; the connect watchdog in
        // StreamResilientAsync bounds that hang as well).
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(idleTimeout);
        try
        {
            return await reader.ReadLineAsync(idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The SSE stream was silent for {idleTimeout.TotalSeconds:0}s (keepalives included) — treating the connection as dead.");
        }
    }

    private static bool IsTransient(HttpStatusCode status)
        => (int)status >= 500
           || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static string? StatusStateOf(RunStreamEvent evt)
    {
        try
        {
            return JsonSerializer.Deserialize<StatusPayload>(
                evt.PayloadJson, JsonSerializerOptions.Web)?.State;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StatusPayload(string? State);

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

    public Task<IReadOnlyList<EnvironmentLayerDto>> ListEnvironmentLayersAsync(
        string? search = null, CancellationToken ct = default)
        => GetAsync<IReadOnlyList<EnvironmentLayerDto>>(
            string.IsNullOrWhiteSpace(search)
                ? "/api/environment-layers"
                : $"/api/environment-layers?search={Uri.EscapeDataString(search)}", ct);

    public Task<EnvironmentLayerDto?> GetEnvironmentLayerAsync(string providerType, CancellationToken ct = default)
        => GetOrNullAsync<EnvironmentLayerDto>(
            $"/api/environment-layers/{Uri.EscapeDataString(providerType)}", ct);

    public Task<EnvironmentLayerDto> UpsertEnvironmentLayerAsync(
        UpsertEnvironmentLayer request, CancellationToken ct = default)
        => PostAsync<UpsertEnvironmentLayer, EnvironmentLayerDto>("/api/environment-layers", request, ct);

    public Task<ProviderCatalogEntry> SetProviderGrantsAsync(
        string providerType, SetProviderGrants request, CancellationToken ct = default)
        => PutAsync<SetProviderGrants, ProviderCatalogEntry>(
            $"/api/provider-catalog/{Uri.EscapeDataString(providerType)}/grants", request, ct);

    public Task<EnvironmentLayerDto> SetEnvironmentLayerGrantsAsync(
        string providerType, SetEnvironmentLayerGrants request, CancellationToken ct = default)
        => PutAsync<SetEnvironmentLayerGrants, EnvironmentLayerDto>(
            $"/api/environment-layers/{Uri.EscapeDataString(providerType)}/grants", request, ct);

    public Task DeleteEnvironmentLayerAsync(string providerType, CancellationToken ct = default)
        => DeleteAsync($"/api/environment-layers/{Uri.EscapeDataString(providerType)}", ct);

    public Task<EnvironmentLayerDto?> DeleteEnvironmentLayerVariantAsync(
        string providerType, string baseEnvironment, CancellationToken ct = default)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.DeleteAsync(
                $"/api/environment-layers/{Uri.EscapeDataString(providerType)}/variants/{Uri.EscapeDataString(baseEnvironment)}",
                token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(response, token);
            return await response.Content.ReadFromJsonAsync<EnvironmentLayerDto>(token);
        });

    // --- Repositories ---

    public Task<IReadOnlyList<WorkspaceResource>> ListWorkspacesAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<WorkspaceResource>>("/api/workspaces", ct);

    public Task<WorkspaceResource?> GetWorkspaceAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<WorkspaceResource>($"/api/workspaces/{id:D}", ct);

    public Task<WorkspaceResource> CreateWorkspaceAsync(CreateWorkspaceResource request, CancellationToken ct = default)
        => PostAsync<CreateWorkspaceResource, WorkspaceResource>("/api/workspaces", request, ct);

    public Task<WorkspaceResource> UpdateWorkspaceAsync(Guid id, UpdateWorkspaceResource request, CancellationToken ct = default)
        => PutAsync<UpdateWorkspaceResource, WorkspaceResource>($"/api/workspaces/{id:D}", request, ct);

    public Task DeleteWorkspaceAsync(Guid id, CancellationToken ct = default)
        => DeleteAsync($"/api/workspaces/{id:D}", ct);

    public Task SetWorkspaceGrantsAsync(Guid id, SetWorkspaceGrants request, CancellationToken ct = default)
        => PostAsync($"/api/workspaces/{id:D}/grants", request, ct);

    // --- Platform settings ---

    public Task<IReadOnlyList<PlatformSettingDto>> ListPlatformSettingsAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<PlatformSettingDto>>("/api/platform-settings", ct);

    public Task<PlatformSettingDto> SetPlatformSettingAsync(
        string key, SetPlatformSetting request, CancellationToken ct = default)
        => PutAsync<SetPlatformSetting, PlatformSettingDto>(
            $"/api/platform-settings/{Uri.EscapeDataString(key)}", request, ct);

    // --- Runner fleet ---

    public Task<IReadOnlyList<RunnerDto>> ListRunnersAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<RunnerDto>>("/api/runners", ct);

    // --- Environment bases ---

    public Task<IReadOnlyList<EnvironmentBaseDto>> ListEnvironmentBasesAsync(
        string? search = null, CancellationToken ct = default)
        => GetAsync<IReadOnlyList<EnvironmentBaseDto>>(
            string.IsNullOrWhiteSpace(search)
                ? "/api/environment-bases"
                : $"/api/environment-bases?search={Uri.EscapeDataString(search)}", ct);

    public Task<EnvironmentBaseDto> UpsertEnvironmentBaseAsync(
        UpsertEnvironmentBase request, CancellationToken ct = default)
        => PostAsync<UpsertEnvironmentBase, EnvironmentBaseDto>("/api/environment-bases", request, ct);

    public Task DeleteEnvironmentBaseAsync(string name, string version, CancellationToken ct = default)
        => DeleteAsync(
            $"/api/environment-bases/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}", ct);

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

    public Task<WorkflowTypeRegistrationDto> SetWorkflowTypeEnabledAsync(
        string workflowType, bool enabled, CancellationToken ct = default)
        => PostAsync<SetWorkflowTypeEnabledRequest, WorkflowTypeRegistrationDto>(
            $"/api/workflow-types/{Uri.EscapeDataString(workflowType)}/enabled",
            new SetWorkflowTypeEnabledRequest(enabled), ct);

    public Task<IReadOnlyList<WorkflowTypeAccessEntryDto>> ListWorkflowTypeAccessAsync(
        string workflowType, CancellationToken ct = default)
        => GetAsync<IReadOnlyList<WorkflowTypeAccessEntryDto>>(
            $"/api/workflow-types/{Uri.EscapeDataString(workflowType)}/access", ct);

    public Task GrantWorkflowTypeAccessAsync(
        string workflowType, WorkflowTypeAccessChange request, CancellationToken ct = default)
        => PostAsync<WorkflowTypeAccessChange, object>(
            $"/api/workflow-types/{Uri.EscapeDataString(workflowType)}/access/grant", request, ct);

    public Task RevokeWorkflowTypeAccessAsync(
        string workflowType, WorkflowTypeAccessChange request, CancellationToken ct = default)
        => PostAsync<WorkflowTypeAccessChange, object>(
            $"/api/workflow-types/{Uri.EscapeDataString(workflowType)}/access/revoke", request, ct);

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

    public Task SetPrincipalTagsAsync(Guid id, SetPrincipalTagsRequest request, CancellationToken ct = default)
        => PostAsync($"/api/principals/{id}/tags", request, ct);

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

    public Task<UserBearerToken> LoginAsync(PasswordLoginRequest request, CancellationToken ct = default)
        => PostAsync<PasswordLoginRequest, UserBearerToken>("/auth/login", request, ct);

    public async Task<ElevationTicket> StepUpAsync(StepUpRequest request, CancellationToken ct = default)
    {
        var ticket = await PostAsync<StepUpRequest, ElevationTicket>("/auth/step-up", request, ct);
        // Hold the elevation on this client: subsequent sensitive calls carry it automatically.
        http.DefaultRequestHeaders.Remove("X-Auxilia-Elevation");
        http.DefaultRequestHeaders.Add("X-Auxilia-Elevation", ticket.Token);
        return ticket;
    }

    public Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<RoleDto>>("/api/roles", ct);

    public Task<SharingSubjects> GetSharingSubjectsAsync(CancellationToken ct = default)
        => GetAsync<SharingSubjects>("/api/directory/subjects", ct);

    public Task<bool> CheckHealthAsync(CancellationToken ct = default)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.GetAsync("/health", token);
            return response.IsSuccessStatusCode;
        });

    // --- Transport helpers (every non-success surfaces as CoreApiException) ---
    // Unary calls run under a linked per-call timeout: HttpClient.Timeout is disabled for the
    // sake of the SSE streams, so this is the only watchdog against a hung request.

    /// <summary>
    /// Runs one unary call under the per-call watchdog. A watchdog expiry surfaces as
    /// <see cref="TimeoutException"/> — never as the caller's cancellation: a slow Core must be
    /// distinguishable from a host shutdown, and every long-running consumer (trigger engines,
    /// intake adapters) keys "stop" on its own token, not on the exception type.
    /// </summary>
    private async Task<TResult> UnaryAsync<TResult>(CancellationToken ct, Func<CancellationToken, Task<TResult>> call)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.UnaryTimeoutSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(options.UnaryTimeoutSeconds));
        try
        {
            return await call(cts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Only the watchdog (or a shared HttpClient's own Timeout) can cancel here.
            throw new TimeoutException(
                $"The Core API call produced no response within {options.UnaryTimeoutSeconds}s.", ex);
        }
    }

    private Task UnaryAsync(CancellationToken ct, Func<CancellationToken, Task> call)
        => UnaryAsync<object?>(ct, async token =>
        {
            await call(token);
            return null;
        });

    private Task<TResult> PostAsync<TRequest, TResult>(string url, TRequest body, CancellationToken ct)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.PostAsJsonAsync(url, body, token);
            await EnsureSuccessAsync(response, token);
            return (await response.Content.ReadFromJsonAsync<TResult>(token))!;
        });

    private Task<TResult> PutAsync<TRequest, TResult>(string url, TRequest body, CancellationToken ct)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.PutAsJsonAsync(url, body, token);
            await EnsureSuccessAsync(response, token);
            return (await response.Content.ReadFromJsonAsync<TResult>(token))!;
        });

    private Task<TResult> PostAsync<TResult>(string url, CancellationToken ct)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.PostAsync(url, null, token);
            await EnsureSuccessAsync(response, token);
            return (await response.Content.ReadFromJsonAsync<TResult>(token))!;
        });

    private Task PostAsync<TRequest>(string url, TRequest body, CancellationToken ct)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.PostAsJsonAsync(url, body, token);
            await EnsureSuccessAsync(response, token);
        });

    private Task PostAsync(string url, CancellationToken ct)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.PostAsync(url, null, token);
            await EnsureSuccessAsync(response, token);
        });

    private Task<TResult> GetAsync<TResult>(string url, CancellationToken ct)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.GetAsync(url, token);
            await EnsureSuccessAsync(response, token);
            return (await response.Content.ReadFromJsonAsync<TResult>(token))!;
        });

    private Task<TResult?> GetOrNullAsync<TResult>(string url, CancellationToken ct) where TResult : class
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.GetAsync(url, token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(response, token);
            return await response.Content.ReadFromJsonAsync<TResult>(token);
        });

    private Task DeleteAsync(string url, CancellationToken ct)
        => UnaryAsync(ct, async token =>
        {
            using var response = await http.DeleteAsync(url, token);
            await EnsureSuccessAsync(response, token);
        });

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

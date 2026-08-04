using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

namespace Auxilia.Workflows.Client.Tests;

/// <summary>
/// Hermetic in-memory <see cref="ICoreClient"/> for the library tests: records dispatches,
/// serves configurable schemas/connectors, and exposes the artifact stream as per-filter
/// channels so tests control exactly what each (filtered) subscriber sees. Members the
/// library never uses throw, so an accidental dependency is caught.
/// </summary>
internal sealed class FakeCoreClient : ICoreClient
{
    // --- Dispatch recording ---
    public ConcurrentQueue<(Guid ConfigurationId, Guid? OnBehalfOf, IReadOnlyDictionary<string, string>? Context)> ConfigurationRuns { get; } = new();
    public ConcurrentQueue<RunRequest> InlineRuns { get; } = new();
    public Exception? DispatchError { get; set; }

    public Task<RunAccepted> RunConfigurationAsync(
        Guid id, Guid? onBehalfOf = null, IReadOnlyDictionary<string, string>? context = null,
        CancellationToken ct = default)
    {
        if (DispatchError is { } error)
            throw error;
        ConfigurationRuns.Enqueue((id, onBehalfOf, context));
        return Task.FromResult(new RunAccepted(Guid.NewGuid(), Guid.NewGuid()));
    }

    public Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default)
    {
        if (DispatchError is { } error)
            throw error;
        InlineRuns.Enqueue(request);
        return Task.FromResult(new RunAccepted(Guid.NewGuid(), Guid.NewGuid()));
    }

    // --- Artifact stream: one channel per subscription, keyed so tests can publish ---
    public sealed record StreamSubscription(
        string? ArtifactType, string? WorkItemId, Channel<ArtifactStreamEvent> Channel);

    public ConcurrentQueue<StreamSubscription> StreamSubscriptions { get; } = new();

    /// <summary>Publishes to every live subscription, applying the SERVER-side filter contract.</summary>
    public void PublishArtifact(ArtifactStreamEvent evt)
    {
        foreach (var subscription in StreamSubscriptions)
        {
            if (subscription.ArtifactType is { } type && type != evt.Artifact.ArtifactType)
                continue;
            if (subscription.WorkItemId is { } workItem && workItem != evt.Artifact.WorkItemId)
                continue;
            subscription.Channel.Writer.TryWrite(evt);
        }
    }

    /// <summary>Completes every open stream (as if the Core recycled) — consumers must reconnect.</summary>
    public void DropAllStreams()
    {
        while (StreamSubscriptions.TryDequeue(out var subscription))
            subscription.Channel.Writer.TryComplete();
    }

    /// <summary>
    /// Emulates the RESILIENT client contract: a dropped subscription (channel completed via
    /// <see cref="DropAllStreams"/>) yields a Reconnecting frame and re-subscribes with the next
    /// attempt number — the enumerable itself never ends on a drop, exactly like CoreClient.
    /// </summary>
    public async IAsyncEnumerable<ClientStreamFrame<ArtifactStreamEvent>> StreamArtifactEventsAsync(
        string? artifactType = null, string? workItemId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            var channel = Channel.CreateUnbounded<ArtifactStreamEvent>();
            StreamSubscriptions.Enqueue(new StreamSubscription(artifactType, workItemId, channel));
            yield return new StreamConnectionFrame<ArtifactStreamEvent>(StreamConnectionState.Connected, attempt);
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                yield return new StreamEventFrame<ArtifactStreamEvent>(evt);
            yield return new StreamConnectionFrame<ArtifactStreamEvent>(
                StreamConnectionState.Reconnecting, attempt, TimeSpan.Zero);
        }
    }

    // --- Artifact store (the catch-up query surface) ---
    public List<ArtifactDto> StoredArtifacts { get; } = [];

    public Task<PagedResult<ArtifactDto>> QueryArtifactsAsync(ArtifactQuery query, CancellationToken ct = default)
    {
        var filtered = StoredArtifacts
            .Where(a => query.ArtifactType is null || a.ArtifactType == query.ArtifactType)
            .Where(a => query.WorkItemId is null || a.WorkItemId == query.WorkItemId)
            .Where(a => query.CreatedAfterUtc is not { } after || a.CreatedUtc > after)
            .OrderBy(a => a.CreatedUtc)
            .ToList();
        var page = filtered.Skip(query.Skip).Take(query.Take).ToList();
        return Task.FromResult(new PagedResult<ArtifactDto>(page, filtered.Count, query.Skip, query.Take));
    }

    // --- Authoring surface ---
    public Dictionary<string, WorkflowSchemaDto> Schemas { get; } = new(StringComparer.Ordinal);
    public HashSet<Guid> KnownConnectors { get; } = [];
    public List<CreateRunConfiguration> CreatedConfigurations { get; } = [];

    public Task<WorkflowSchemaDto?> GetWorkflowSchemaAsync(string workflowType, CancellationToken ct = default)
        => Task.FromResult(Schemas.GetValueOrDefault(workflowType));

    public Task<Connector?> GetConnectorAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<Connector?>(KnownConnectors.Contains(id)
            ? new Connector(id, "fake", "fake-provider", [], DateTimeOffset.UtcNow)
            : null);

    public Task<RunConfiguration> CreateConfigurationAsync(CreateRunConfiguration request, CancellationToken ct = default)
    {
        CreatedConfigurations.Add(request);
        return Task.FromResult(new RunConfiguration(
            Guid.NewGuid(), request.Name, request.WorkflowType,
            request.Context ?? new Dictionary<string, string>(),
            request.SlotBindings ?? [], request.Enabled, DateTimeOffset.UtcNow, request.Tags ?? []));
    }

    // --- Unused by the library ---
    private static T Nope<T>() => throw new NotSupportedException("Not used by Auxilia.Workflows.Client.");

    public Task<RunConfiguration> UpdateConfigurationAsync(Guid id, UpdateRunConfiguration request, CancellationToken ct = default) => Nope<Task<RunConfiguration>>();
    public Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default) => Nope<Task<RunConfiguration?>>();
    public Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default) => Nope<Task<PagedResult<RunConfiguration>>>();
    public Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default) => Nope<Task<RunStatus?>>();
    public Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default) => Nope<Task<PagedResult<RunStatus>>>();
    public Task CancelRunAsync(Guid id, CancellationToken ct = default) => Nope<Task>();
    public Task<RunAccepted> RerunAsync(Guid id, CancellationToken ct = default) => Nope<Task<RunAccepted>>();
    public IAsyncEnumerable<ClientStreamFrame<RunStreamEvent>> StreamRunAsync(Guid runId, CancellationToken ct = default) => Nope<IAsyncEnumerable<ClientStreamFrame<RunStreamEvent>>>();
    public Task<PagedResult<RunViewItem>> GetRunViewsAsync(Guid runId, string? view = null, int skip = 0, int take = 200, CancellationToken ct = default) => Nope<Task<PagedResult<RunViewItem>>>();
    public Task<RunStats> GetRunStatsAsync(CancellationToken ct = default) => Nope<Task<RunStats>>();
    public Task<IReadOnlyList<DashboardPin>> ListDashboardPinsAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<DashboardPin>>>();
    public Task<DashboardPin> PinDashboardViewAsync(CreateDashboardPin request, CancellationToken ct = default) => Nope<Task<DashboardPin>>();
    public Task UnpinDashboardViewAsync(Guid pinId, CancellationToken ct = default) => Nope<Task>();
    public Task ProvideInputAsync(Guid runId, string payloadJson, CancellationToken ct = default) => Nope<Task>();
    public Task<TerminalTicket> OpenTerminalAsync(Guid runId, CancellationToken ct = default) => Nope<Task<TerminalTicket>>();
    public Task<int> ClearFinishedRunsAsync(CancellationToken ct = default) => Nope<Task<int>>();
    public Task<ArtifactDto?> GetArtifactAsync(Guid id, CancellationToken ct = default) => Nope<Task<ArtifactDto?>>();
    public Task<Stream?> OpenArtifactContentAsync(Guid id, CancellationToken ct = default) => Nope<Task<Stream?>>();
    public Task<PagedResult<AuditEntry>> QueryAuditAsync(AuditQuery query, CancellationToken ct = default) => Nope<Task<PagedResult<AuditEntry>>>();
    public Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default) => Nope<Task<Connector>>();
    public Task<Connector> UpdateConnectorAsync(Guid id, UpdateConnector request, CancellationToken ct = default) => Nope<Task<Connector>>();
    public Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default) => Nope<Task<PagedResult<Connector>>>();
    public Task SetConnectorGrantsAsync(Guid id, SetConnectorGrants request, CancellationToken ct = default) => Nope<Task>();
    public Task<RunConfiguration> SetConfigurationGrantsAsync(Guid id, SetConfigurationGrants request, CancellationToken ct = default) => Nope<Task<RunConfiguration>>();
    public Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<RoleDto>>>();
    public Task<SharingSubjects> GetSharingSubjectsAsync(CancellationToken ct = default) => Nope<Task<SharingSubjects>>();
    public Task<ConnectorBrowseResult> BrowseConnectorAsync(Guid id, BrowseConnector request, CancellationToken ct = default) => Nope<Task<ConnectorBrowseResult>>();
    public List<ProviderCatalogEntry> ProviderCatalog { get; } = [];
    public Task<PagedResult<ProviderCatalogEntry>> QueryProviderCatalogAsync(ProviderCatalogQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<ProviderCatalogEntry>(ProviderCatalog, ProviderCatalog.Count, 0, ProviderCatalog.Count));
    public Task<ProviderCatalogEntry> RegisterProviderAsync(RegisterSlotProvider request, CancellationToken ct = default) => Nope<Task<ProviderCatalogEntry>>();
    public Task<ProviderCatalogEntry> SetProviderAvailabilityAsync(string providerType, bool available, CancellationToken ct = default) => Nope<Task<ProviderCatalogEntry>>();
    public Task<ProviderCatalogEntry> SetProviderSettingDisabledAsync(string providerType, string settingKey, bool disabled, CancellationToken ct = default) => Nope<Task<ProviderCatalogEntry>>();
    public Task<IReadOnlyList<EnvironmentLayerDto>> ListEnvironmentLayersAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<EnvironmentLayerDto>>>();
    public Task<EnvironmentLayerDto?> GetEnvironmentLayerAsync(string providerType, CancellationToken ct = default) => Nope<Task<EnvironmentLayerDto?>>();
    public Task<EnvironmentLayerDto> UpsertEnvironmentLayerAsync(UpsertEnvironmentLayer request, CancellationToken ct = default) => Nope<Task<EnvironmentLayerDto>>();
    public Task<PagedResult<WorkflowTypeDto>> ListWorkflowTypesAsync(WorkflowTypeQuery query, CancellationToken ct = default) => Nope<Task<PagedResult<WorkflowTypeDto>>>();
    public Task<WorkflowTypeRegistrationDto> RegisterWorkflowTypeAsync(RegisterWorkflowTypeRequest request, CancellationToken ct = default) => Nope<Task<WorkflowTypeRegistrationDto>>();
    public Task<WorkflowTypeRegistrationDto?> GetWorkflowTypeRegistrationAsync(string workflowType, CancellationToken ct = default) => Nope<Task<WorkflowTypeRegistrationDto?>>();
    public Task<WorkflowTypeRegistrationDto> SetWorkflowTypeEnabledAsync(string workflowType, bool enabled, CancellationToken ct = default) => Nope<Task<WorkflowTypeRegistrationDto>>();
    public Task<WorkflowTypeRegistrationDto> ApproveWorkflowTypeAsync(string workflowType, CancellationToken ct = default) => Nope<Task<WorkflowTypeRegistrationDto>>();
    public Task<WorkflowTypeRegistrationDto> DenyWorkflowTypeAsync(string workflowType, string reason, CancellationToken ct = default) => Nope<Task<WorkflowTypeRegistrationDto>>();
    public Task<GroupDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default) => Nope<Task<GroupDto>>();
    public Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<GroupDto>>>();
    public Task<PagedResult<PrincipalDto>> QueryPrincipalsAsync(PrincipalQuery query, CancellationToken ct = default) => Nope<Task<PagedResult<PrincipalDto>>>();
    public Task<PrincipalDto?> GetPrincipalAsync(Guid id, CancellationToken ct = default) => Nope<Task<PrincipalDto?>>();
    public Task<PrincipalDto> CreateHumanPrincipalAsync(CreateHumanPrincipalRequest request, CancellationToken ct = default) => Nope<Task<PrincipalDto>>();
    public Task<CreatedApiKeyPrincipal> CreateApiKeyPrincipalAsync(CreateApiKeyPrincipalRequest request, CancellationToken ct = default) => Nope<Task<CreatedApiKeyPrincipal>>();
    public Task<IReadOnlyList<GroupMappingDto>> ListGroupMappingsAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<GroupMappingDto>>>();
    public Task<GroupMappingDto> CreateGroupMappingAsync(CreateGroupMappingRequest request, CancellationToken ct = default) => Nope<Task<GroupMappingDto>>();
    public Task<IReadOnlyList<IdentityConnectorDescriptorDto>> ListIdentityConnectorsAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<IdentityConnectorDescriptorDto>>>();
    public Task<IReadOnlyList<IdentitySourceDto>> ListIdentitySourcesAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<IdentitySourceDto>>>();
    public Task<IdentitySourceDto?> GetIdentitySourceAsync(Guid id, CancellationToken ct = default) => Nope<Task<IdentitySourceDto?>>();
    public Task<IdentitySourceDto> SaveIdentitySourceAsync(SaveIdentitySourceRequest request, CancellationToken ct = default) => Nope<Task<IdentitySourceDto>>();
    public Task<IdentityConnectorTestResult> TestIdentitySourceAsync(Guid id, CancellationToken ct = default) => Nope<Task<IdentityConnectorTestResult>>();
    public Task<IdentityImportSummaryDto> ImportIdentitySourceAsync(Guid id, CancellationToken ct = default) => Nope<Task<IdentityImportSummaryDto>>();
    public Task<CurrentPrincipal> GetCurrentPrincipalAsync(CancellationToken ct = default) => Nope<Task<CurrentPrincipal>>();
    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Nope<Task<bool>>();
    public Task AssignGroupRoleAsync(Guid groupId, AssignGroupRoleRequest request, CancellationToken ct = default) => Nope<Task>();
    public Task AddGroupMemberAsync(Guid groupId, AddGroupMemberRequest request, CancellationToken ct = default) => Nope<Task>();
    public Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default) => Nope<Task>();
    public Task AssignPrincipalRoleAsync(Guid id, AssignRoleRequest request, CancellationToken ct = default) => Nope<Task>();
    public Task RevokePrincipalRoleAsync(Guid id, string roleName, CancellationToken ct = default) => Nope<Task>();
    public Task SetPrincipalEnabledAsync(Guid id, SetPrincipalEnabledRequest request, CancellationToken ct = default) => Nope<Task>();
    public Task SetPrincipalTagsAsync(Guid id, SetPrincipalTagsRequest request, CancellationToken ct = default) => Nope<Task>();
    public Task<ElevationTicket> StepUpAsync(StepUpRequest request, CancellationToken ct = default) => Nope<Task<ElevationTicket>>();
    public Task<UserBearerToken> LoginAsync(PasswordLoginRequest request, CancellationToken ct = default) => Nope<Task<UserBearerToken>>();
    public Task DeleteConfigurationAsync(Guid id, CancellationToken ct = default) => Nope<Task>();
    public Task DeleteConnectorAsync(Guid id, CancellationToken ct = default) => Nope<Task>();
    public Task DeleteProviderAsync(string providerType, CancellationToken ct = default) => Nope<Task>();
    public Task DeleteEnvironmentLayerAsync(string providerType, CancellationToken ct = default) => Nope<Task>();
    public Task<IReadOnlyList<EnvironmentBaseDto>> ListEnvironmentBasesAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<EnvironmentBaseDto>>>();
    public Task<IReadOnlyList<PlatformSettingDto>> ListPlatformSettingsAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<PlatformSettingDto>>>();
    public Task<PlatformSettingDto> SetPlatformSettingAsync(string key, SetPlatformSetting request, CancellationToken ct = default) => Nope<Task<PlatformSettingDto>>();
    public Task<IReadOnlyList<WorkspaceResource>> ListWorkspacesAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<WorkspaceResource>>>();
    public Task<WorkspaceResource?> GetWorkspaceAsync(Guid id, CancellationToken ct = default) => Nope<Task<WorkspaceResource?>>();
    public Task<WorkspaceResource> CreateWorkspaceAsync(CreateWorkspaceResource request, CancellationToken ct = default) => Nope<Task<WorkspaceResource>>();
    public Task<WorkspaceResource> UpdateWorkspaceAsync(Guid id, UpdateWorkspaceResource request, CancellationToken ct = default) => Nope<Task<WorkspaceResource>>();
    public Task DeleteWorkspaceAsync(Guid id, CancellationToken ct = default) => Nope<Task>();
    public Task SetWorkspaceGrantsAsync(Guid id, SetWorkspaceGrants request, CancellationToken ct = default) => Nope<Task>();
    public Task<EnvironmentBaseDto> UpsertEnvironmentBaseAsync(UpsertEnvironmentBase request, CancellationToken ct = default) => Nope<Task<EnvironmentBaseDto>>();
    public Task DeleteEnvironmentBaseAsync(string name, string version, CancellationToken ct = default) => Nope<Task>();
    public Task DeleteIdentitySourceAsync(Guid id, CancellationToken ct = default) => Nope<Task>();
    public Task UnregisterWorkflowTypeAsync(string workflowType, CancellationToken ct = default) => Nope<Task>();
}

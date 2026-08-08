using System.Net;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// Hermetic in-memory <see cref="ICoreClient"/> for console component tests: no HTTP, no Core. Only the
/// members the console pages under test use are meaningful; everything else throws so an accidental
/// dependency is caught. <see cref="QueryAuditAsync"/> emulates the Core's exact-match + paging filter.
/// </summary>
internal sealed class FakeCoreClient : ICoreClient
{
    public List<AuditEntry> AuditEntries { get; } = [];
    public AuditQuery? LastAuditQuery { get; private set; }
    public int QueryAuditCallCount { get; private set; }

    public CurrentPrincipal CurrentPrincipal { get; set; } =
        new(Guid.NewGuid(), "Operator test user", ["Operator"],
            [PermissionActions.SlotConfigWrite, PermissionActions.WorkflowConfigurationManage,
             PermissionActions.WorkflowTrigger, PermissionActions.RunObserve]);
    public bool Healthy { get; set; } = true;
    public CoreApiException? AuditError { get; set; }

    public Task<PagedResult<AuditEntry>> QueryAuditAsync(AuditQuery query, CancellationToken ct = default)
    {
        QueryAuditCallCount++;
        LastAuditQuery = query;
        if (AuditError is { } error)
            throw error;

        var filtered = AuditEntries
            .OrderByDescending(e => e.TimestampUtc)
            .Where(e => query.Actor is null || e.Actor == query.Actor)
            .Where(e => query.Action is null || e.Action == query.Action)
            .Where(e => query.Subject is null || e.Subject == query.Subject)
            .Where(e => query.FromUtc is not { } from || e.TimestampUtc >= from)
            .Where(e => query.ToUtc is not { } to || e.TimestampUtc < to)
            .ToList();

        var page = filtered.Skip(query.Skip).Take(query.Take).ToList();
        return Task.FromResult(new PagedResult<AuditEntry>(page, filtered.Count, query.Skip, query.Take));
    }

    public Task<CurrentPrincipal> GetCurrentPrincipalAsync(CancellationToken ct = default)
        => Task.FromResult(CurrentPrincipal);

    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(Healthy);

    // --- Principals (Admin page) ---
    public List<PrincipalDto> Principals { get; } = [];
    public PrincipalQuery? LastPrincipalQuery { get; private set; }
    public CreateHumanPrincipalRequest? LastCreatedHuman { get; private set; }
    public CreateApiKeyPrincipalRequest? LastCreatedApiKey { get; private set; }
    public string CreatedApiKeyValue { get; set; } = "auxk_generated_once";
    public List<(Guid Id, string Role)> AssignedRoles { get; } = [];
    public List<(Guid Id, string Role)> RevokedRoles { get; } = [];
    public List<(Guid Id, bool Enabled)> EnabledChanges { get; } = [];
    public CoreApiException? PrincipalsError { get; set; }

    public Task<PagedResult<PrincipalDto>> QueryPrincipalsAsync(PrincipalQuery query, CancellationToken ct = default)
    {
        LastPrincipalQuery = query;
        if (PrincipalsError is { } error)
            throw error;
        var filtered = Principals
            .Where(p => query.Kind is null || p.Kind == query.Kind)
            .Where(p => query.Enabled is not { } enabled || (p.Status == "Active") == enabled)
            .Where(p => query.Search is null || p.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var page = filtered.Skip(query.Skip).Take(query.Take).ToList();
        return Task.FromResult(new PagedResult<PrincipalDto>(page, filtered.Count, query.Skip, query.Take));
    }

    public Task<PrincipalDto> CreateHumanPrincipalAsync(CreateHumanPrincipalRequest request, CancellationToken ct = default)
    {
        LastCreatedHuman = request;
        var created = new PrincipalDto(Guid.NewGuid(), "Human", request.DisplayName, "Active", request.Username, [], []);
        Principals.Add(created);
        return Task.FromResult(created);
    }

    public Task<CreatedApiKeyPrincipal> CreateApiKeyPrincipalAsync(CreateApiKeyPrincipalRequest request, CancellationToken ct = default)
    {
        LastCreatedApiKey = request;
        var principal = new PrincipalDto(Guid.NewGuid(), "Service", request.DisplayName, "Active", null, [], []);
        Principals.Add(principal);
        return Task.FromResult(new CreatedApiKeyPrincipal(principal, CreatedApiKeyValue));
    }

    /// <summary>True = principal mutations demand step-up until <see cref="StepUpAsync"/> succeeds.</summary>
    public bool RequireElevation { get; set; }

    public string? LastStepUpSecret { get; private set; }

    private bool _elevated;

    private void DemandElevationIfRequired()
    {
        if (RequireElevation && !_elevated)
            throw new CoreApiException(
                System.Net.HttpStatusCode.Forbidden, "elevation-required", "step-up required");
    }

    public Task AssignPrincipalRoleAsync(Guid id, AssignRoleRequest request, CancellationToken ct = default)
    {
        DemandElevationIfRequired();
        AssignedRoles.Add((id, request.RoleName));
        return Task.CompletedTask;
    }

    public Task RevokePrincipalRoleAsync(Guid id, string roleName, CancellationToken ct = default)
    {
        RevokedRoles.Add((id, roleName));
        return Task.CompletedTask;
    }

    public Task SetPrincipalEnabledAsync(Guid id, SetPrincipalEnabledRequest request, CancellationToken ct = default)
    {
        DemandElevationIfRequired();
        EnabledChanges.Add((id, request.Enabled));
        return Task.CompletedTask;
    }

    public List<(Guid Id, IReadOnlyList<string> Tags)> TagChanges { get; } = [];

    public Task SetPrincipalTagsAsync(Guid id, SetPrincipalTagsRequest request, CancellationToken ct = default)
    {
        TagChanges.Add((id, request.Tags));
        return Task.CompletedTask;
    }

    public Task<UserBearerToken> LoginAsync(PasswordLoginRequest request, CancellationToken ct = default)
        => Task.FromResult(new UserBearerToken("auxu_fake", DateTimeOffset.UtcNow.AddHours(8)));

    /// <summary>When set, every step-up attempt fails with this error detail (wrong-secret path).</summary>
    public string? StepUpError { get; set; }

    public Task<ElevationTicket> StepUpAsync(StepUpRequest request, CancellationToken ct = default)
    {
        LastStepUpSecret = request.Secret;
        if (StepUpError is { } detail)
            throw new CoreApiException(HttpStatusCode.Forbidden, detail, "step-up failed");
        _elevated = true;
        return Task.FromResult(new ElevationTicket("fake-elevation", DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    // --- Identity sources ---
    public List<IdentityConnectorDescriptorDto> IdentityConnectors { get; } = [];
    public List<IdentitySourceDto> IdentitySources { get; } = [];
    public SaveIdentitySourceRequest? LastSavedSource { get; private set; }
    public List<Guid> ImportedSources { get; } = [];
    public List<Guid> TestedSources { get; } = [];
    public List<Guid> DeletedSources { get; } = [];
    public IdentityImportSummaryDto ImportResult { get; set; } = new(3, 1, 0, 2, []);
    public IdentityConnectorTestResult TestResult { get; set; } = new(true, "reachable — 42 users");
    public CoreApiException? IdentitySourcesError { get; set; }

    public Task<IReadOnlyList<IdentityConnectorDescriptorDto>> ListIdentityConnectorsAsync(CancellationToken ct = default)
    {
        if (IdentitySourcesError is { } error)
            throw error;
        return Task.FromResult<IReadOnlyList<IdentityConnectorDescriptorDto>>(IdentityConnectors);
    }

    public Task<IReadOnlyList<IdentitySourceDto>> ListIdentitySourcesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IdentitySourceDto>>(IdentitySources);

    public Task<IdentitySourceDto?> GetIdentitySourceAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(IdentitySources.FirstOrDefault(s => s.Id == id));

    public Task<IdentitySourceDto> SaveIdentitySourceAsync(SaveIdentitySourceRequest request, CancellationToken ct = default)
    {
        LastSavedSource = request;
        var saved = new IdentitySourceDto(
            Guid.NewGuid(), request.Name, request.ConnectorType, request.DisableMissing, request.DefaultRole,
            request.GroupRoleMappings, request.Settings, [], null, null);
        return Task.FromResult(saved);
    }

    public Task DeleteIdentitySourceAsync(Guid id, CancellationToken ct = default)
    {
        DeletedSources.Add(id);
        return Task.CompletedTask;
    }

    public Task<IdentityConnectorTestResult> TestIdentitySourceAsync(Guid id, CancellationToken ct = default)
    {
        TestedSources.Add(id);
        return Task.FromResult(TestResult);
    }

    public Task<IdentityImportSummaryDto> ImportIdentitySourceAsync(Guid id, CancellationToken ct = default)
    {
        ImportedSources.Add(id);
        return Task.FromResult(ImportResult);
    }

    // --- Provider catalog ---
    public List<ProviderCatalogEntry> ProviderCatalog { get; } = [];
    public List<(string ProviderType, bool Available)> AvailabilityChanges { get; } = [];
    public List<(string ProviderType, string Key, bool Disabled)> SettingChanges { get; } = [];
    public CoreApiException? ProviderCatalogError { get; set; }

    public Task<PagedResult<ProviderCatalogEntry>> QueryProviderCatalogAsync(ProviderCatalogQuery query, CancellationToken ct = default)
    {
        if (ProviderCatalogError is { } error)
            throw error;
        var items = ProviderCatalog.Skip(query.Skip).Take(query.Take).ToList();
        return Task.FromResult(new PagedResult<ProviderCatalogEntry>(items, ProviderCatalog.Count, query.Skip, query.Take));
    }

    public Task<ProviderCatalogEntry> SetProviderAvailabilityAsync(string providerType, bool available, CancellationToken ct = default)
    {
        AvailabilityChanges.Add((providerType, available));
        var entry = ProviderCatalog.First(e => e.ProviderType == providerType) with { Available = available };
        var index = ProviderCatalog.FindIndex(e => e.ProviderType == providerType);
        ProviderCatalog[index] = entry;
        return Task.FromResult(entry);
    }

    public Task<ProviderCatalogEntry> SetProviderSettingDisabledAsync(string providerType, string settingKey, bool disabled, CancellationToken ct = default)
    {
        SettingChanges.Add((providerType, settingKey, disabled));
        return Task.FromResult(ProviderCatalog.First(e => e.ProviderType == providerType));
    }

    // --- Runs ---
    public List<RunStatus> Runs { get; } = [];
    public List<RunConfiguration> Configurations { get; } = [];
    public RunQuery? LastRunQuery { get; private set; }
    public List<Guid> CancelledRuns { get; } = [];
    /// <summary>Thrown by <see cref="QueryRunsAsync"/> — a CoreApiException OR a raw transport exception.</summary>
    public Exception? RunsError { get; set; }

    public Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default)
    {
        LastRunQuery = query;
        if (RunsError is { } error)
            throw error;
        var filtered = Runs
            .Where(r => query.State is null || r.State == query.State)
            .Where(r => query.WorkflowType is null || r.WorkflowType == query.WorkflowType)
            .OrderByDescending(r => r.CreatedUtc)
            .ToList();
        var page = filtered.Skip(query.Skip).Take(query.Take).ToList();
        return Task.FromResult(new PagedResult<RunStatus>(page, filtered.Count, query.Skip, query.Take));
    }

    public Task<RunAccepted> RerunAsync(Guid id, CancellationToken ct = default)
    {
        var commandId = Guid.NewGuid();
        return Task.FromResult(new RunAccepted(commandId, commandId));
    }

    public Task CancelRunAsync(Guid id, CancellationToken ct = default)
    {
        CancelledRuns.Add(id);
        return Task.CompletedTask;
    }

    public Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Configurations.FirstOrDefault(c => c.Id == id));

    // --- Run configurations (Workflows page + editor) ---
    public ConfigurationQuery? LastConfigurationQuery { get; private set; }
    public CreateRunConfiguration? LastCreatedConfiguration { get; private set; }
    public List<(Guid Id, Guid? OnBehalfOf)> RunConfigurationCalls { get; } = [];
    public CoreApiException? ConfigurationsError { get; set; }

    public Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default)
    {
        LastConfigurationQuery = query;
        if (ConfigurationsError is { } error)
            throw error;
        var filtered = Configurations
            .Where(c => query.WorkflowType is null || c.WorkflowType == query.WorkflowType)
            .Where(c => query.Enabled is not { } enabled || c.Enabled == enabled)
            .OrderByDescending(c => c.UpdatedUtc)
            .ToList();
        var page = filtered.Skip(query.Skip).Take(query.Take).ToList();
        return Task.FromResult(new PagedResult<RunConfiguration>(page, filtered.Count, query.Skip, query.Take));
    }

    public Task<RunConfiguration> CreateConfigurationAsync(CreateRunConfiguration request, CancellationToken ct = default)
    {
        LastCreatedConfiguration = request;
        var created = new RunConfiguration(
            Guid.NewGuid(), request.Name, request.WorkflowType,
            request.Context ?? new Dictionary<string, string>(), request.SlotBindings ?? [],
            request.Enabled, DateTimeOffset.UtcNow);
        Configurations.Add(created);
        return Task.FromResult(created);
    }

    public UpdateRunConfiguration? LastUpdatedConfiguration { get; private set; }
    public Guid? LastUpdatedConfigurationId { get; private set; }

    public Task<RunConfiguration> UpdateConfigurationAsync(
        Guid id, UpdateRunConfiguration request, CancellationToken ct = default)
    {
        LastUpdatedConfiguration = request;
        LastUpdatedConfigurationId = id;
        var existing = Configurations.First(c => c.Id == id);
        var updated = existing with
        {
            Name = request.Name ?? existing.Name,
            Context = request.Context ?? existing.Context,
            SlotBindings = request.SlotBindings ?? existing.SlotBindings,
            Enabled = request.Enabled ?? existing.Enabled,
            Tags = request.Tags ?? existing.Tags,
        };
        Configurations[Configurations.IndexOf(existing)] = updated;
        return Task.FromResult(updated);
    }

    public Task<RunAccepted> RunConfigurationAsync(Guid id, Guid? onBehalfOf = null, IReadOnlyDictionary<string, string>? context = null, CancellationToken ct = default)
    {
        RunConfigurationCalls.Add((id, onBehalfOf));
        if (ConfigurationsError is { } error)
            throw error;
        return Task.FromResult(new RunAccepted(Guid.NewGuid(), Guid.NewGuid()));
    }

    // --- Run detail: GetRunAsync + scripted SSE stream ---
    /// <summary>Scripted SSE events yielded by <see cref="StreamRunAsync"/> (in order) for any run id.</summary>
    public List<RunStreamEvent> StreamEvents { get; } = [];

    /// <summary>
    /// Optional FULL-frame script (connection + event frames); when non-empty it wins over
    /// <see cref="StreamEvents"/>. Lets tests drive the reconnect UX deterministically.
    /// </summary>
    public List<ClientStreamFrame<RunStreamEvent>> StreamFrames { get; } = [];

    public CoreApiException? GetRunError { get; set; }
    public int GetRunCalls { get; private set; }

    public Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default)
    {
        GetRunCalls++;
        if (GetRunError is { } error)
            throw error;
        return Task.FromResult(Runs.FirstOrDefault(r => r.RunId == id));
    }

    public async IAsyncEnumerable<ClientStreamFrame<RunStreamEvent>> StreamRunAsync(
        Guid runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var frames = StreamFrames.Count > 0
            ? StreamFrames.ToList()
            : new List<ClientStreamFrame<RunStreamEvent>> { new StreamConnectionFrame<RunStreamEvent>(StreamConnectionState.Connected, 1) }
                .Concat(StreamEvents.Select(e => (ClientStreamFrame<RunStreamEvent>)new StreamEventFrame<RunStreamEvent>(e)))
                .ToList();
        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return frame;
        }
    }

    // --- Connectors ---
    public List<Connector> Connectors { get; } = [];
    public ConnectorQuery? LastConnectorQuery { get; private set; }
    public CreateConnector? LastCreatedConnector { get; private set; }
    public List<(Guid Id, SetConnectorGrants Request)> GrantCalls { get; } = [];
    public CoreApiException? ConnectorsError { get; set; }

    public Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default)
    {
        LastConnectorQuery = query;
        if (ConnectorsError is { } error)
            throw error;
        var filtered = Connectors
            .Where(c => query.ProviderType is null || c.ProviderType == query.ProviderType)
            .ToList();
        var page = filtered.Skip(query.Skip).Take(query.Take).ToList();
        return Task.FromResult(new PagedResult<Connector>(page, filtered.Count, query.Skip, query.Take));
    }

    public Task<Connector?> GetConnectorAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Connectors.FirstOrDefault(c => c.Id == id));

    public Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default)
    {
        LastCreatedConnector = request;
        var created = new Connector(
            Guid.NewGuid(), request.Name, request.ProviderType, request.Settings.Keys.ToList(),
            DateTimeOffset.UtcNow, request.Scope);
        Connectors.Add(created);
        return Task.FromResult(created);
    }

    public Task<Connector> UpdateConnectorAsync(Guid id, UpdateConnector request, CancellationToken ct = default)
    {
        var existing = Connectors.First(c => c.Id == id);
        var keys = existing.SettingKeys.Union(request.Settings?.Keys ?? []).ToList();
        var updated = existing with { Name = request.Name ?? existing.Name, SettingKeys = keys };
        Connectors[Connectors.IndexOf(existing)] = updated;
        return Task.FromResult(updated);
    }

    public Task SetConnectorGrantsAsync(Guid id, SetConnectorGrants request, CancellationToken ct = default)
    {
        GrantCalls.Add((id, request));
        return Task.CompletedTask;
    }

    public Task<RunConfiguration> SetConfigurationGrantsAsync(
        Guid id, SetConfigurationGrants request, CancellationToken ct = default)
    {
        ConfigurationGrantCalls.Add((id, request));
        var existing = Configurations.First(c => c.Id == id);
        var updated = existing with { Grants = request.Grants };
        Configurations[Configurations.IndexOf(existing)] = updated;
        return Task.FromResult(updated);
    }

    public List<(Guid Id, SetConfigurationGrants Request)> ConfigurationGrantCalls { get; } = [];

    public List<RoleDto> Roles { get; } =
    [
        new("Administrator", []), new("Operator", []), new("User", []), new("Auditor", [])
    ];

    public SharingSubjects SharingSubjects { get; set; } = new([], []);

    public Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RoleDto>>(Roles);

    public Task<SharingSubjects> GetSharingSubjectsAsync(CancellationToken ct = default)
        => Task.FromResult(SharingSubjects);

    // --- Unused by the pages under test ---
    private static T Nope<T>() => throw new NotSupportedException("Not needed for these tests.");

    public Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default) => Nope<Task<RunAccepted>>();
    public Task<PagedResult<ArtifactDto>> QueryArtifactsAsync(ArtifactQuery query, CancellationToken ct = default) => Nope<Task<PagedResult<ArtifactDto>>>();
    public Task<ArtifactDto?> GetArtifactAsync(Guid id, CancellationToken ct = default) => Nope<Task<ArtifactDto?>>();
    public Task<Stream?> OpenArtifactContentAsync(Guid id, CancellationToken ct = default) => Nope<Task<Stream?>>();
    public IAsyncEnumerable<ClientStreamFrame<ArtifactStreamEvent>> StreamArtifactEventsAsync(string? artifactType = null, string? workItemId = null, CancellationToken ct = default) => Nope<IAsyncEnumerable<ClientStreamFrame<ArtifactStreamEvent>>>();
    // --- Events (Events page + RunDetail strip) ---
    public List<EventDto> StoredEvents { get; } = [];
    public EventQuery? LastEventQuery { get; private set; }
    public CoreApiException? EventsError { get; set; }

    public Task<PagedResult<EventDto>> QueryEventsAsync(EventQuery query, CancellationToken ct = default)
    {
        LastEventQuery = query;
        if (EventsError is { } error)
            throw error;
        var filtered = StoredEvents
            .Where(e => query.EventType is null || e.EventType == query.EventType)
            .Where(e => query.WorkItemId is null || e.WorkItemId == query.WorkItemId)
            .Where(e => query.SourceRunId is not { } run || e.SourceRunId == run)
            .OrderByDescending(e => e.CreatedUtc)
            .ToList();
        var page = filtered.Skip(query.Skip).Take(query.Take).ToList();
        return Task.FromResult(new PagedResult<EventDto>(page, filtered.Count, query.Skip, query.Take));
    }
    /// <summary>Scripted frames yielded by <see cref="StreamEventsAsync"/> — connection frames included.</summary>
    public List<ClientStreamFrame<EventStreamEvent>> EventStreamFrames { get; } = [];

    public (string? EventType, string? WorkItemId)? LastEventStreamFilter { get; private set; }

    public async IAsyncEnumerable<ClientStreamFrame<EventStreamEvent>> StreamEventsAsync(
        string? eventType = null, string? workItemId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        LastEventStreamFilter = (eventType, workItemId);
        foreach (var frame in EventStreamFrames.ToList())
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return frame;
        }
    }
    public Task<GroupDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default) => Nope<Task<GroupDto>>();
    public Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<GroupDto>>>();
    public Task AddGroupMemberAsync(Guid groupId, AddGroupMemberRequest request, CancellationToken ct = default) => Nope<Task>();
    public Task AssignGroupRoleAsync(Guid groupId, AssignGroupRoleRequest request, CancellationToken ct = default) => Nope<Task>();
    public Task<PrincipalDto?> GetPrincipalAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Principals.FirstOrDefault(p => p.Id == id));
    public Task<IReadOnlyList<GroupMappingDto>> ListGroupMappingsAsync(CancellationToken ct = default) => Nope<Task<IReadOnlyList<GroupMappingDto>>>();
    public Task<GroupMappingDto> CreateGroupMappingAsync(CreateGroupMappingRequest request, CancellationToken ct = default) => Nope<Task<GroupMappingDto>>();
    public Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default) => Nope<Task>();

    // --- Workflow types + schemas ---
    public List<WorkflowTypeDto> WorkflowTypes { get; } = [];
    public Dictionary<string, WorkflowSchemaDto> WorkflowSchemas { get; } = [];
    public WorkflowTypeQuery? LastWorkflowTypeQuery { get; private set; }
    public CoreApiException? WorkflowTypesError { get; set; }

    public Task<PagedResult<WorkflowTypeDto>> ListWorkflowTypesAsync(WorkflowTypeQuery query, CancellationToken ct = default)
    {
        LastWorkflowTypeQuery = query;
        if (WorkflowTypesError is { } error)
            throw error;
        var filtered = WorkflowTypes.Where(t => query.Status is null || t.Status == query.Status).ToList();
        var page = filtered.Skip(query.Skip).Take(query.Take).ToList();
        return Task.FromResult(new PagedResult<WorkflowTypeDto>(page, filtered.Count, query.Skip, query.Take));
    }

    public Task<WorkflowSchemaDto?> GetWorkflowSchemaAsync(string workflowType, CancellationToken ct = default)
        => Task.FromResult(WorkflowSchemas.GetValueOrDefault(workflowType));

    public List<RunViewItem> RunViews { get; } = [];
    public List<DashboardPin> DashboardPins { get; } = [];

    public Task<RunStats> GetRunStatsAsync(CancellationToken ct = default)
    {
        var byState = Runs.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        var active = Runs.Count(r => r.State is not ("Success" or "Failed" or "Cancelled" or "PreFlightFailed"));
        return Task.FromResult(new RunStats(Runs.Count, active, byState));
    }

    public Task<IReadOnlyList<DashboardPin>> ListDashboardPinsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DashboardPin>>(DashboardPins.ToList());

    public Task<DashboardPin> PinDashboardViewAsync(CreateDashboardPin request, CancellationToken ct = default)
    {
        var run = Runs.First(r => r.RunId == request.RunId);
        var pin = new DashboardPin(Guid.NewGuid(), request.RunId, request.ViewName, run.WorkflowType, DateTimeOffset.UtcNow);
        DashboardPins.RemoveAll(p => p.RunId == request.RunId && p.ViewName == request.ViewName);
        DashboardPins.Add(pin);
        return Task.FromResult(pin);
    }

    public Task UnpinDashboardViewAsync(Guid pinId, CancellationToken ct = default)
    {
        DashboardPins.RemoveAll(p => p.Id == pinId);
        return Task.CompletedTask;
    }

    public int GetRunViewsCalls { get; private set; }

    public Task<PagedResult<RunViewItem>> GetRunViewsAsync(
        Guid runId, string? view = null, int skip = 0, int take = 200, CancellationToken ct = default)
    {
        GetRunViewsCalls++;
        var matching = RunViews.Where(v => view is null || v.ViewName == view).ToList();
        var page = matching.Skip(skip).Take(take).ToList();
        return Task.FromResult(new PagedResult<RunViewItem>(page, matching.Count, skip, take));
    }

    public Task<ProviderCatalogEntry> RegisterProviderAsync(RegisterSlotProvider request, CancellationToken ct = default)
        => Task.FromResult(new ProviderCatalogEntry(request.ProviderType, false, request.Category, [], request.Contracts, request.Description));

    public Task DeleteConfigurationAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteProviderAsync(string providerType, CancellationToken ct = default) => Task.CompletedTask;

    // --- Environments (layers + bases), the Environments admin tab ---
    public List<EnvironmentLayerDto> EnvironmentLayers { get; } = [];
    public List<UpsertEnvironmentLayer> UpsertedLayers { get; } = [];
    public List<string> DeletedLayers { get; } = [];
    public List<(string ProviderType, IReadOnlyList<AccessGrant> Grants)> LayerGrantCalls { get; } = [];
    public CoreApiException? EnvironmentsError { get; set; }

    public Task<IReadOnlyList<EnvironmentLayerDto>> ListEnvironmentLayersAsync(string? search = null, CancellationToken ct = default)
    {
        if (EnvironmentsError is { } error)
            throw error;
        var matching = EnvironmentLayers
            .Where(l => search is null || l.ProviderType.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult<IReadOnlyList<EnvironmentLayerDto>>(matching);
    }

    public Task<EnvironmentLayerDto?> GetEnvironmentLayerAsync(string providerType, CancellationToken ct = default)
        => Task.FromResult(EnvironmentLayers.FirstOrDefault(l => l.ProviderType == providerType));

    public Task<EnvironmentLayerDto> UpsertEnvironmentLayerAsync(UpsertEnvironmentLayer request, CancellationToken ct = default)
    {
        UpsertedLayers.Add(request);
        var index = EnvironmentLayers.FindIndex(l => l.ProviderType == request.ProviderType);
        var grants = index >= 0 ? EnvironmentLayers[index].Grants : [];
        var saved = new EnvironmentLayerDto(
            request.ProviderType, request.Description, request.BaseEnvironment, request.SetupScript,
            request.Version, DateTimeOffset.UtcNow, request.BaseVersion) { Grants = grants };
        if (index >= 0)
            EnvironmentLayers[index] = saved;
        else
            EnvironmentLayers.Add(saved);
        return Task.FromResult(saved);
    }

    public Task DeleteEnvironmentLayerAsync(string providerType, CancellationToken ct = default)
    {
        DeletedLayers.Add(providerType);
        EnvironmentLayers.RemoveAll(l => l.ProviderType == providerType);
        return Task.CompletedTask;
    }

    public Task<EnvironmentLayerDto> SetEnvironmentLayerGrantsAsync(string providerType, SetEnvironmentLayerGrants request, CancellationToken ct = default)
    {
        DemandElevationIfRequired();
        LayerGrantCalls.Add((providerType, request.Grants));
        var index = EnvironmentLayers.FindIndex(l => l.ProviderType == providerType);
        var updated = EnvironmentLayers[index] with { Grants = request.Grants };
        EnvironmentLayers[index] = updated;
        return Task.FromResult(updated);
    }

    public List<(string ProviderType, IReadOnlyList<AccessGrant> Grants)> ProviderGrantCalls { get; } = [];

    public Task<ProviderCatalogEntry> SetProviderGrantsAsync(string providerType, SetProviderGrants request, CancellationToken ct = default)
    {
        DemandElevationIfRequired();
        ProviderGrantCalls.Add((providerType, request.Grants));
        var index = ProviderCatalog.FindIndex(e => e.ProviderType == providerType);
        var updated = ProviderCatalog[index] with { Grants = request.Grants };
        ProviderCatalog[index] = updated;
        return Task.FromResult(updated);
    }

    // --- Workflow-type access list ---
    public List<WorkflowTypeAccessEntryDto> WorkflowTypeAccess { get; } = [];
    public List<(string Type, WorkflowTypeAccessChange Change)> AccessGrantCalls { get; } = [];
    public List<(string Type, WorkflowTypeAccessChange Change)> AccessRevokeCalls { get; } = [];

    public Task<IReadOnlyList<WorkflowTypeAccessEntryDto>> ListWorkflowTypeAccessAsync(string workflowType, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkflowTypeAccessEntryDto>>(WorkflowTypeAccess.ToList());

    public Task GrantWorkflowTypeAccessAsync(string workflowType, WorkflowTypeAccessChange request, CancellationToken ct = default)
    {
        AccessGrantCalls.Add((workflowType, request));
        WorkflowTypeAccess.Add(new WorkflowTypeAccessEntryDto(
            request.Action, request.RoleName, request.PrincipalId, request.GroupId));
        return Task.CompletedTask;
    }

    public Task RevokeWorkflowTypeAccessAsync(string workflowType, WorkflowTypeAccessChange request, CancellationToken ct = default)
    {
        AccessRevokeCalls.Add((workflowType, request));
        WorkflowTypeAccess.RemoveAll(e => e.Action == request.Action
                                          && e.RoleName == request.RoleName
                                          && e.PrincipalId == request.PrincipalId
                                          && e.GroupId == request.GroupId);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<WorkspaceResource>> ListWorkspacesAsync(CancellationToken ct = default)
        => Nope<Task<IReadOnlyList<WorkspaceResource>>>();
    public Task<WorkspaceResource?> GetWorkspaceAsync(Guid id, CancellationToken ct = default)
        => Nope<Task<WorkspaceResource?>>();
    public Task<WorkspaceResource> CreateWorkspaceAsync(CreateWorkspaceResource request, CancellationToken ct = default)
        => Nope<Task<WorkspaceResource>>();
    public Task<WorkspaceResource> UpdateWorkspaceAsync(Guid id, UpdateWorkspaceResource request, CancellationToken ct = default)
        => Nope<Task<WorkspaceResource>>();
    public Task DeleteWorkspaceAsync(Guid id, CancellationToken ct = default) => Nope<Task>();
    public Task SetWorkspaceGrantsAsync(Guid id, SetWorkspaceGrants request, CancellationToken ct = default)
        => Nope<Task>();
    public Task<IReadOnlyList<PlatformSettingDto>> ListPlatformSettingsAsync(CancellationToken ct = default)
        => Nope<Task<IReadOnlyList<PlatformSettingDto>>>();
    public Task<PlatformSettingDto> SetPlatformSettingAsync(string key, SetPlatformSetting request, CancellationToken ct = default)
        => Nope<Task<PlatformSettingDto>>();
    public List<EnvironmentBaseDto> EnvironmentBases { get; } = [];
    public List<UpsertEnvironmentBase> UpsertedBases { get; } = [];
    public List<(string Name, string Version)> DeletedBases { get; } = [];

    public Task<IReadOnlyList<EnvironmentBaseDto>> ListEnvironmentBasesAsync(string? search = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EnvironmentBaseDto>>(EnvironmentBases.ToList());

    public Task<EnvironmentBaseDto> UpsertEnvironmentBaseAsync(UpsertEnvironmentBase request, CancellationToken ct = default)
    {
        UpsertedBases.Add(request);
        var saved = new EnvironmentBaseDto(request.Name, request.Version, request.Description, DateTimeOffset.UtcNow);
        EnvironmentBases.RemoveAll(b => b.Name == request.Name && b.Version == request.Version);
        EnvironmentBases.Add(saved);
        return Task.FromResult(saved);
    }

    public Task DeleteEnvironmentBaseAsync(string name, string version, CancellationToken ct = default)
    {
        DeletedBases.Add((name, version));
        EnvironmentBases.RemoveAll(b => b.Name == name && b.Version == version);
        return Task.CompletedTask;
    }
    public Task DeleteConnectorAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ConnectorBrowseResult> BrowseConnectorAsync(Guid id, BrowseConnector request, CancellationToken ct = default)
        => Task.FromResult(new ConnectorBrowseResult([]));

    public Task ProvideInputAsync(Guid runId, string payloadJson, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<TerminalTicket> OpenTerminalAsync(Guid runId, CancellationToken ct = default)
        => Task.FromResult(new TerminalTicket(
            runId, $"/api/runs/{runId}/terminal/?ticket=fake", DateTimeOffset.UtcNow.AddMinutes(2)));
    public Task<int> ClearFinishedRunsAsync(CancellationToken ct = default)
        => Task.FromResult(0);

    // --- Workflow-type registry (AdminWorkflowTypes page) ---
    public Dictionary<string, WorkflowTypeRegistrationDto> WorkflowTypeRegistrations { get; } = new(StringComparer.Ordinal);
    public RegisterWorkflowTypeRequest? LastRegisteredWorkflowType { get; private set; }
    public List<string> ApprovedWorkflowTypes { get; } = [];
    public List<(string Type, string Reason)> DeniedWorkflowTypes { get; } = [];
    public List<string> UnregisteredWorkflowTypes { get; } = [];
    public List<(string Type, bool Enabled)> WorkflowTypeEnabledChanges { get; } = [];
    /// <summary>Status a newly registered type lands in (trusted signature = Active, else Pending).</summary>
    public string RegisteredWorkflowTypeStatus { get; set; } = WorkflowTypeStatus.Pending;

    private WorkflowTypeRegistrationDto SetWorkflowTypeStatus(string workflowType, string status, string? reason = null)
    {
        var index = WorkflowTypes.FindIndex(t => t.WorkflowType == workflowType);
        if (index >= 0)
            WorkflowTypes[index] = WorkflowTypes[index] with { Status = status };
        var registration = new WorkflowTypeRegistrationDto(
            workflowType, null, status, reason, null, false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        WorkflowTypeRegistrations[workflowType] = registration;
        return registration;
    }

    public Task<WorkflowTypeRegistrationDto> RegisterWorkflowTypeAsync(RegisterWorkflowTypeRequest request, CancellationToken ct = default)
    {
        LastRegisteredWorkflowType = request;
        WorkflowTypes.Add(new WorkflowTypeDto(
            request.WorkflowType, "1.0", "Job", null, [], request.PackageUri, RegisteredWorkflowTypeStatus));
        return Task.FromResult(SetWorkflowTypeStatus(request.WorkflowType, RegisteredWorkflowTypeStatus));
    }

    public Task<WorkflowTypeRegistrationDto?> GetWorkflowTypeRegistrationAsync(string workflowType, CancellationToken ct = default)
        => Task.FromResult(WorkflowTypeRegistrations.GetValueOrDefault(workflowType));

    public Task UnregisterWorkflowTypeAsync(string workflowType, CancellationToken ct = default)
    {
        DemandElevationIfRequired();
        UnregisteredWorkflowTypes.Add(workflowType);
        WorkflowTypes.RemoveAll(t => t.WorkflowType == workflowType);
        WorkflowTypeRegistrations.Remove(workflowType);
        return Task.CompletedTask;
    }

    public Task<WorkflowTypeRegistrationDto> SetWorkflowTypeEnabledAsync(string workflowType, bool enabled, CancellationToken ct = default)
    {
        WorkflowTypeEnabledChanges.Add((workflowType, enabled));
        return Task.FromResult(SetWorkflowTypeStatus(
            workflowType, enabled ? WorkflowTypeStatus.Active : WorkflowTypeStatus.Disabled));
    }

    public Task<WorkflowTypeRegistrationDto> ApproveWorkflowTypeAsync(string workflowType, CancellationToken ct = default)
    {
        ApprovedWorkflowTypes.Add(workflowType);
        return Task.FromResult(SetWorkflowTypeStatus(workflowType, WorkflowTypeStatus.Active));
    }

    public Task<WorkflowTypeRegistrationDto> DenyWorkflowTypeAsync(string workflowType, string reason, CancellationToken ct = default)
    {
        DeniedWorkflowTypes.Add((workflowType, reason));
        return Task.FromResult(SetWorkflowTypeStatus(workflowType, WorkflowTypeStatus.Denied, reason));
    }
}

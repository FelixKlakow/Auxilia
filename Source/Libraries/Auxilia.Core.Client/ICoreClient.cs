using Auxilia.Core.Contracts;

namespace Auxilia.Core.Client;

/// <summary>
/// Typed client for the Core API — the single way products, CLIs, and automation drive the Core:
/// dispatch and observe runs, manage stored configurations and connectors, administer identity
/// (groups, directory group→role mappings), and read the authenticated principal. Realizes the
/// "product is a pure Core client" boundary from the separation plan. Every call authorizes against
/// the caller's principal; a non-success response surfaces as a <see cref="CoreApiException"/>.
/// </summary>
public interface ICoreClient
{
    // --- Run configurations ---
    Task<RunConfiguration> CreateConfigurationAsync(CreateRunConfiguration request, CancellationToken ct = default);
    /// <summary>Updates a stored configuration in place; null request fields stay unchanged.</summary>
    Task<RunConfiguration> UpdateConfigurationAsync(Guid id, UpdateRunConfiguration request, CancellationToken ct = default);
    Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default);
    /// <summary>Deletes a stored configuration permanently.</summary>
    Task DeleteConfigurationAsync(Guid id, CancellationToken ct = default);
    /// <summary>Replaces a personal configuration's access grants (owner or a configuration manager).</summary>
    Task<RunConfiguration> SetConfigurationGrantsAsync(Guid id, SetConfigurationGrants request, CancellationToken ct = default);
    Task<RunAccepted> RunConfigurationAsync(
        Guid id, Guid? onBehalfOf = null, IReadOnlyDictionary<string, string>? context = null,
        CancellationToken ct = default);

    // --- Runs ---
    Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default);
    Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default);
    Task CancelRunAsync(Guid id, CancellationToken ct = default);
    /// <summary>Re-dispatches a past run from its stored dispatch command as a NEW run.</summary>
    Task<RunAccepted> RerunAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Live-view stream for a run: status transitions and view items as discriminated
    /// <see cref="RunStreamEvent"/>s, over Server-Sent Events. The sequence completes when the run
    /// reaches a terminal state or <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<RunStreamEvent> StreamRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>A run's persisted view items (outputs) — the read-later counterpart of the stream.</summary>
    Task<PagedResult<RunViewItem>> GetRunViewsAsync(
        Guid runId, string? view = null, int skip = 0, int take = 200, CancellationToken ct = default);

    /// <summary>Server-side aggregate run counts for dashboards.</summary>
    Task<RunStats> GetRunStatsAsync(CancellationToken ct = default);

    // --- Dashboard pins (personal to the calling principal) ---
    Task<IReadOnlyList<DashboardPin>> ListDashboardPinsAsync(CancellationToken ct = default);
    /// <summary>Pins a run view to the caller's dashboard (idempotent per run+view).</summary>
    Task<DashboardPin> PinDashboardViewAsync(CreateDashboardPin request, CancellationToken ct = default);
    Task UnpinDashboardViewAsync(Guid pinId, CancellationToken ct = default);

    /// <summary>
    /// Delivers an opaque input (guidance / a decision / halt) into a running workflow — the
    /// steer-back half of the loop. The Core authorizes and audits; it never interprets the payload.
    /// </summary>
    Task ProvideInputAsync(Guid runId, string payloadJson, CancellationToken ct = default);

    /// <summary>
    /// Mints a short-lived ticket for the run's interactive web terminal and returns the
    /// ready-to-open (absolute) proxy URL. Requires the run.open-terminal permission; only
    /// runs whose status reports <see cref="RunStatus.HasTerminal"/> have one.
    /// </summary>
    Task<TerminalTicket> OpenTerminalAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Deletes all finished (terminal) runs and their persisted views; returns how many.</summary>
    Task<int> ClearFinishedRunsAsync(CancellationToken ct = default);

    // --- Artifacts ---
    /// <summary>Queries persisted-artifact metadata (newest first); requires the artifact.consume permission.</summary>
    Task<PagedResult<ArtifactDto>> QueryArtifactsAsync(ArtifactQuery query, CancellationToken ct = default);
    Task<ArtifactDto?> GetArtifactAsync(Guid id, CancellationToken ct = default);
    /// <summary>Opens an artifact's payload; null when the artifact (or its payload) is unknown.</summary>
    Task<Stream?> OpenArtifactContentAsync(Guid id, CancellationToken ct = default);
    /// <summary>
    /// Artifact events over Server-Sent Events, SERVER-SIDE FILTERED by artifact type and/or work
    /// item — the client-surface way to chain on artifacts (no message-bus access required).
    /// Runs until <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<ArtifactStreamEvent> StreamArtifactEventsAsync(
        string? artifactType = null, string? workItemId = null, CancellationToken ct = default);

    // --- Audit ---
    /// <summary>Queries the Core's centralized audit log (newest first); requires the audit-read permission.</summary>
    Task<PagedResult<AuditEntry>> QueryAuditAsync(AuditQuery query, CancellationToken ct = default);

    // --- Connectors ---
    Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default);
    /// <summary>Renames a connector and/or upserts settings key-by-key — the credential-refresh path.</summary>
    Task<Connector> UpdateConnectorAsync(Guid id, UpdateConnector request, CancellationToken ct = default);
    Task<Connector?> GetConnectorAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default);
    Task SetConnectorGrantsAsync(Guid id, SetConnectorGrants request, CancellationToken ct = default);
    /// <summary>Deletes a connector permanently (owner or connector manager).</summary>
    Task DeleteConnectorAsync(Guid id, CancellationToken ct = default);
    /// <summary>Browses live data with a connector's credential (repositories / branches); Core-side only.</summary>
    Task<ConnectorBrowseResult> BrowseConnectorAsync(Guid id, BrowseConnector request, CancellationToken ct = default);

    // --- Provider catalog (Core-owned governance of slot-provider availability; requires provider-catalog.manage) ---
    Task<PagedResult<ProviderCatalogEntry>> QueryProviderCatalogAsync(ProviderCatalogQuery query, CancellationToken ct = default);
    /// <summary>Registers (or updates) a provider descriptor; availability stays deny-by-default.</summary>
    Task<ProviderCatalogEntry> RegisterProviderAsync(RegisterSlotProvider request, CancellationToken ct = default);
    /// <summary>Removes a provider from the catalog entirely.</summary>
    Task DeleteProviderAsync(string providerType, CancellationToken ct = default);
    Task<ProviderCatalogEntry> SetProviderAvailabilityAsync(string providerType, bool available, CancellationToken ct = default);
    Task<ProviderCatalogEntry> SetProviderSettingDisabledAsync(string providerType, string settingKey, bool disabled, CancellationToken ct = default);

    // --- Environment layers (admin-managed session software; requires provider-catalog.manage) ---
    Task<IReadOnlyList<EnvironmentLayerDto>> ListEnvironmentLayersAsync(CancellationToken ct = default);
    Task<EnvironmentLayerDto?> GetEnvironmentLayerAsync(string providerType, CancellationToken ct = default);
    /// <summary>Creates or updates a layer AND its (available) catalog entry.</summary>
    Task<EnvironmentLayerDto> UpsertEnvironmentLayerAsync(UpsertEnvironmentLayer request, CancellationToken ct = default);
    Task DeleteEnvironmentLayerAsync(string providerType, CancellationToken ct = default);

    // --- Workflow-type registry (types are registered permanently with their signed package; only
    //     Active types run; reads require workflow-configuration.manage, writes workflow-type.manage,
    //     approve/deny the workflow-type.sign signing authority) ---
    /// <summary>Lists the registered workflow types (optionally filtered by status).</summary>
    Task<PagedResult<WorkflowTypeDto>> ListWorkflowTypesAsync(WorkflowTypeQuery query, CancellationToken ct = default);
    /// <summary>The full schema (slots, capabilities, inputs, views) of a workflow type, or null if unregistered.</summary>
    Task<WorkflowSchemaDto?> GetWorkflowSchemaAsync(string workflowType, CancellationToken ct = default);
    /// <summary>Registers a workflow type; trusted-signed packages activate immediately, others enter Pending.</summary>
    Task<WorkflowTypeRegistrationDto> RegisterWorkflowTypeAsync(RegisterWorkflowTypeRequest request, CancellationToken ct = default);
    /// <summary>The registry's administrative view of a type (status, package, publisher key), or null.</summary>
    Task<WorkflowTypeRegistrationDto?> GetWorkflowTypeRegistrationAsync(string workflowType, CancellationToken ct = default);
    /// <summary>Removes a type from the registry; it can no longer be run.</summary>
    Task UnregisterWorkflowTypeAsync(string workflowType, CancellationToken ct = default);
    /// <summary>Operational on/off switch: a disabled type stops dispatching until re-enabled.</summary>
    Task<WorkflowTypeRegistrationDto> SetWorkflowTypeEnabledAsync(string workflowType, bool enabled, CancellationToken ct = default);
    /// <summary>Signing authority: accepts a pending registration; the type becomes Active.</summary>
    Task<WorkflowTypeRegistrationDto> ApproveWorkflowTypeAsync(string workflowType, CancellationToken ct = default);
    /// <summary>Signing authority: refuses a registration with a recorded reason.</summary>
    Task<WorkflowTypeRegistrationDto> DenyWorkflowTypeAsync(string workflowType, string reason, CancellationToken ct = default);

    // --- Groups ---
    Task<GroupDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken ct = default);
    Task AddGroupMemberAsync(Guid groupId, AddGroupMemberRequest request, CancellationToken ct = default);
    Task AssignGroupRoleAsync(Guid groupId, AssignGroupRoleRequest request, CancellationToken ct = default);

    // --- Principals (identity administration; requires principal.administer) ---
    Task<PagedResult<PrincipalDto>> QueryPrincipalsAsync(PrincipalQuery query, CancellationToken ct = default);
    Task<PrincipalDto?> GetPrincipalAsync(Guid id, CancellationToken ct = default);
    Task<PrincipalDto> CreateHumanPrincipalAsync(CreateHumanPrincipalRequest request, CancellationToken ct = default);
    /// <summary>Creates an AI/service principal; the returned API key is shown once and never retrievable again.</summary>
    Task<CreatedApiKeyPrincipal> CreateApiKeyPrincipalAsync(CreateApiKeyPrincipalRequest request, CancellationToken ct = default);
    Task AssignPrincipalRoleAsync(Guid id, AssignRoleRequest request, CancellationToken ct = default);
    Task RevokePrincipalRoleAsync(Guid id, string roleName, CancellationToken ct = default);
    Task SetPrincipalEnabledAsync(Guid id, SetPrincipalEnabledRequest request, CancellationToken ct = default);
    /// <summary>Replaces a principal's free-form tags (the classification axis); empty clears them.</summary>
    Task SetPrincipalTagsAsync(Guid id, SetPrincipalTagsRequest request, CancellationToken ct = default);

    // --- Directory group → role mappings (federated sign-in) ---
    Task<IReadOnlyList<GroupMappingDto>> ListGroupMappingsAsync(CancellationToken ct = default);
    Task<GroupMappingDto> CreateGroupMappingAsync(CreateGroupMappingRequest request, CancellationToken ct = default);
    Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default);

    // --- Identity sources (bulk/offline principal provisioning from LDAP/AD or CSV; requires identity-source.manage) ---
    Task<IReadOnlyList<IdentityConnectorDescriptorDto>> ListIdentityConnectorsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<IdentitySourceDto>> ListIdentitySourcesAsync(CancellationToken ct = default);
    Task<IdentitySourceDto?> GetIdentitySourceAsync(Guid id, CancellationToken ct = default);
    Task<IdentitySourceDto> SaveIdentitySourceAsync(SaveIdentitySourceRequest request, CancellationToken ct = default);
    Task DeleteIdentitySourceAsync(Guid id, CancellationToken ct = default);
    Task<IdentityConnectorTestResult> TestIdentitySourceAsync(Guid id, CancellationToken ct = default);
    /// <summary>Runs the import for a source (idempotent upsert of principals) and returns the run summary.</summary>
    Task<IdentityImportSummaryDto> ImportIdentitySourceAsync(Guid id, CancellationToken ct = default);

    // --- Identity / diagnostics ---
    /// <summary>The principal the client is authenticated as, with its resolved roles.</summary>
    Task<CurrentPrincipal> GetCurrentPrincipalAsync(CancellationToken ct = default);
    /// <summary>The built-in roles and the permission actions each grants.</summary>
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default);
    /// <summary>Principals + first-class groups for sharing pickers (ids and display names only).</summary>
    Task<SharingSubjects> GetSharingSubjectsAsync(CancellationToken ct = default);
    /// <summary>True when the Core reports healthy; never throws (for connectivity checks).</summary>
    Task<bool> CheckHealthAsync(CancellationToken ct = default);
}

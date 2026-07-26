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
    Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default);
    Task<RunAccepted> RunConfigurationAsync(
        Guid id, Guid? onBehalfOf = null, IReadOnlyDictionary<string, string>? context = null,
        CancellationToken ct = default);

    // --- Runs ---
    Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default);
    Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default);
    Task CancelRunAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Live-view stream for a run: status transitions and view items as discriminated
    /// <see cref="RunStreamEvent"/>s, over Server-Sent Events. The sequence completes when the run
    /// reaches a terminal state or <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<RunStreamEvent> StreamRunAsync(Guid runId, CancellationToken ct = default);

    // --- Audit ---
    /// <summary>Queries the Core's centralized audit log (newest first); requires the audit-read permission.</summary>
    Task<PagedResult<AuditEntry>> QueryAuditAsync(AuditQuery query, CancellationToken ct = default);

    // --- Connectors ---
    Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default);
    Task<Connector?> GetConnectorAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default);
    Task SetConnectorGrantsAsync(Guid id, SetConnectorGrants request, CancellationToken ct = default);

    // --- Provider catalog (Core-owned governance of slot-provider availability; requires provider-catalog.manage) ---
    Task<PagedResult<ProviderCatalogEntry>> QueryProviderCatalogAsync(ProviderCatalogQuery query, CancellationToken ct = default);
    Task<ProviderCatalogEntry> SetProviderAvailabilityAsync(string providerType, bool available, CancellationToken ct = default);
    Task<ProviderCatalogEntry> SetProviderSettingDisabledAsync(string providerType, string settingKey, bool disabled, CancellationToken ct = default);

    // --- Workflow types + schemas (Core schema registry; requires workflow-configuration.manage) ---
    /// <summary>Lists the registered workflow types the config editor can configure.</summary>
    Task<PagedResult<WorkflowTypeDto>> ListWorkflowTypesAsync(WorkflowTypeQuery query, CancellationToken ct = default);
    /// <summary>The full schema (slots, capabilities, inputs, views) of a workflow type, or null if unregistered.</summary>
    Task<WorkflowSchemaDto?> GetWorkflowSchemaAsync(string workflowType, CancellationToken ct = default);

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
    /// <summary>True when the Core reports healthy; never throws (for connectivity checks).</summary>
    Task<bool> CheckHealthAsync(CancellationToken ct = default);
}

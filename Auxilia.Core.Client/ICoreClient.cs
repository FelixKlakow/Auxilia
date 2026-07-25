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
    Task<RunAccepted> RunConfigurationAsync(Guid id, CancellationToken ct = default);

    // --- Runs ---
    Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default);
    Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default);
    Task CancelRunAsync(Guid id, CancellationToken ct = default);

    // --- Connectors ---
    Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default);
    Task<Connector?> GetConnectorAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default);
    Task SetConnectorGrantsAsync(Guid id, SetConnectorGrants request, CancellationToken ct = default);

    // --- Groups ---
    Task<GroupDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken ct = default);
    Task AddGroupMemberAsync(Guid groupId, AddGroupMemberRequest request, CancellationToken ct = default);
    Task AssignGroupRoleAsync(Guid groupId, AssignGroupRoleRequest request, CancellationToken ct = default);

    // --- Directory group → role mappings (federated sign-in) ---
    Task<IReadOnlyList<GroupMappingDto>> ListGroupMappingsAsync(CancellationToken ct = default);
    Task<GroupMappingDto> CreateGroupMappingAsync(CreateGroupMappingRequest request, CancellationToken ct = default);
    Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default);

    // --- Identity / diagnostics ---
    /// <summary>The principal the client is authenticated as, with its resolved roles.</summary>
    Task<CurrentPrincipal> GetCurrentPrincipalAsync(CancellationToken ct = default);
    /// <summary>True when the Core reports healthy; never throws (for connectivity checks).</summary>
    Task<bool> CheckHealthAsync(CancellationToken ct = default);
}

using Auxilia.Core.Contracts;

namespace Auxilia.Core.Client;

/// <summary>
/// Typed client for the Core API — the single way products, CLIs, and automation drive the
/// Core (create configurations, dispatch and observe runs, manage connectors). Realizes the
/// "product is a pure Core client" boundary from the separation plan.
/// </summary>
public interface ICoreClient
{
    Task<RunConfiguration> CreateConfigurationAsync(CreateRunConfiguration request, CancellationToken ct = default);
    Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default);
    Task<RunAccepted> RunConfigurationAsync(Guid id, CancellationToken ct = default);
    Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default);
    Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default);
    Task CancelRunAsync(Guid id, CancellationToken ct = default);
    Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default);
    Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default);
}

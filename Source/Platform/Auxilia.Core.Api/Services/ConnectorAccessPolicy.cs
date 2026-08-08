using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Decides whether a principal may bind a connector into a run. A <see cref="ResourceScope.Company"/>
/// connector is usable by anyone; a <see cref="ResourceScope.Personal"/> (identity-linked) connector
/// is usable only by its owner and the subjects its grants admit (<see cref="AccessGrantEvaluator"/>).
/// </summary>
public sealed class ConnectorAccessPolicy(
    IDataAccess<CoreConnectorRecord> connectors,
    AccessGrantEvaluator grants)
{
    public async Task<bool> CanUseAsync(Guid connectorId, Guid? principalId, CancellationToken ct)
    {
        // A missing connector is not an access failure here — the resolver reports it as not-found.
        if (await connectors.ReadAsync(connectorId, ct) is not { } connector)
            return true;
        if (connector.Scope != ResourceScope.Personal)
            return true;
        if (principalId is not { } pid)
            return false; // a personal connector needs an identified principal to authorize.
        if (connector.OwnerPrincipalId == pid)
            return true;
        return await grants.IsGrantedAsync(connector.GrantsJson, pid, ct);
    }
}

/// <summary>
/// Thrown at dispatch when the triggering principal may not use a resource the run references
/// (a connector, the workflow type itself, an environment layer). Endpoints map it to 403.
/// </summary>
public class RunAccessDeniedException(string message) : Exception(message);

/// <summary>Thrown at dispatch when the triggering principal may not use a connector a slot binds.</summary>
public sealed class ConnectorAccessDeniedException(Guid connectorId)
    : RunAccessDeniedException($"not permitted to use connector '{connectorId:D}'")
{
    public Guid ConnectorId { get; } = connectorId;
}

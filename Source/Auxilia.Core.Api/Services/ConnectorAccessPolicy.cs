using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Decides whether a principal may bind a connector into a run. A <see cref="ConnectorScope.Company"/>
/// connector is usable by anyone; a <see cref="ConnectorScope.Personal"/> (identity-linked) connector
/// is usable only by its owner, principals it grants directly, and principals whose directory (AD)
/// group membership includes a granted group. The directory groups come from the principal's last
/// federated sign-in (<see cref="PrincipalRecord.DirectoryGroupsJson"/>) — this is the AD cascade.
/// </summary>
public sealed class ConnectorAccessPolicy(
    IDataAccess<CoreConnectorRecord> connectors,
    IDataAccess<PrincipalRecord> principals)
{
    public async Task<bool> CanUseAsync(Guid connectorId, Guid? principalId, CancellationToken ct)
    {
        // A missing connector is not an access failure here — the resolver reports it as not-found.
        if (await connectors.ReadAsync(connectorId, ct) is not { } connector)
            return true;
        if (connector.Scope != ConnectorScope.Personal)
            return true;
        if (principalId is not { } pid)
            return false; // a personal connector needs an identified principal to authorize.
        if (connector.OwnerPrincipalId == pid)
            return true;

        var grants = JsonSerializer.Deserialize<List<ConnectorGrant>>(connector.GrantsJson) ?? [];
        if (grants.Any(g => g.Kind == ConnectorGrantKind.Principal
                            && Guid.TryParse(g.Id, out var granted) && granted == pid))
            return true;

        var groupGrants = grants
            .Where(g => g.Kind == ConnectorGrantKind.DirectoryGroup)
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groupGrants.Count == 0)
            return false;

        var principal = await principals.ReadAsync(pid, ct);
        var principalGroups = JsonSerializer.Deserialize<List<string>>(principal?.DirectoryGroupsJson ?? "[]") ?? [];
        return principalGroups.Any(groupGrants.Contains);
    }
}

/// <summary>Thrown at dispatch when the triggering principal may not use a connector a slot binds.</summary>
public sealed class ConnectorAccessDeniedException(Guid connectorId)
    : Exception($"not permitted to use connector '{connectorId:D}'")
{
    public Guid ConnectorId { get; } = connectorId;
}

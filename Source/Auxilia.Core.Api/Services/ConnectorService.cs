using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Owns credential-bearing connectors. Setting values are protected on write and only ever
/// decrypted for dispatch-time injection via <see cref="ResolveSettingsAsync"/> — read endpoints
/// expose keys, never values.
/// </summary>
public sealed class ConnectorService(
    IDataAccess<CoreConnectorRecord> store,
    ISettingsProtector protector,
    TimeProvider clock)
{
    /// <summary>
    /// Creates a connector. A <see cref="ConnectorScope.Personal"/> connector is owned by
    /// <paramref name="ownerPrincipalId"/> (identity-linked); company connectors carry no owner.
    /// </summary>
    public async Task<Connector> CreateAsync(CreateConnector request, Guid? ownerPrincipalId, CancellationToken ct)
    {
        var protectedSettings = request.Settings.ToDictionary(kv => kv.Key, kv => protector.Protect(kv.Value));
        var personal = request.Scope == ConnectorScope.Personal;
        var record = new CoreConnectorRecord
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            ProviderType = request.ProviderType,
            ProtectedSettingsJson = JsonSerializer.Serialize(protectedSettings),
            Scope = personal ? ConnectorScope.Personal : ConnectorScope.Company,
            OwnerPrincipalId = personal ? ownerPrincipalId : null,
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(record, ct);
        return ToDto(record);
    }

    /// <summary>Replaces a connector's access grants. Returns false when the connector is unknown.</summary>
    public async Task<bool> SetGrantsAsync(Guid id, IReadOnlyList<ConnectorGrant> grants, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } record)
            return false;
        await store.SaveAsync(
            record with { GrantsJson = JsonSerializer.Serialize(grants), UpdatedUtc = clock.GetUtcNow() }, ct);
        return true;
    }

    public async Task<Connector?> GetAsync(Guid id, CancellationToken ct)
        => await store.ReadAsync(id, ct) is { } r ? ToDto(r) : null;

    public async Task<PagedResult<Connector>> QueryAsync(ConnectorQuery query, CancellationToken ct)
    {
        var all = (await store.ReadAsync(ct)).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.ProviderType))
            all = all.Where(c => c.ProviderType == query.ProviderType);
        var ordered = all.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var page = ordered.Skip(query.Skip).Take(query.Take).Select(ToDto).ToList();
        return new PagedResult<Connector>(page, ordered.Count, query.Skip, query.Take);
    }

    /// <summary>
    /// Decrypts a connector's settings for just-in-time injection. Never call this from a read
    /// endpoint — the plaintext must not leave the Core except into a launching container.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> ResolveSettingsAsync(Guid id, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } r)
            return null;
        var protectedSettings = JsonSerializer.Deserialize<Dictionary<string, string>>(r.ProtectedSettingsJson)
                                ?? new Dictionary<string, string>();
        return protectedSettings.ToDictionary(kv => kv.Key, kv => protector.Unprotect(kv.Value));
    }

    private static Connector ToDto(CoreConnectorRecord r)
    {
        var keys = (JsonSerializer.Deserialize<Dictionary<string, string>>(r.ProtectedSettingsJson)
                    ?? new Dictionary<string, string>()).Keys.ToList();
        var grants = JsonSerializer.Deserialize<List<ConnectorGrant>>(r.GrantsJson) ?? [];
        return new Connector(r.Id, r.Name, r.ProviderType, keys, r.UpdatedUtc, r.Scope, r.OwnerPrincipalId, grants);
    }
}

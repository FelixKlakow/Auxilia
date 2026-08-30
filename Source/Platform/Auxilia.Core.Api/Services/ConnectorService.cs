using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>The caller a connector read is filtered for: connector managers see every connector.</summary>
public readonly record struct ConnectorViewer(Guid? PrincipalId, bool SeesAll);

/// <summary>
/// Owns credential-bearing connectors. Setting values are protected on write and only ever
/// decrypted for dispatch-time injection via <see cref="ResolveSettingsAsync"/> — read endpoints
/// expose keys, never values. A Personal connector belongs to its owner and is visible only to
/// the owner, granted subjects (<see cref="AccessGrantEvaluator"/>), and connector managers —
/// the same ownership model as run configurations.
/// </summary>
public sealed class ConnectorService(
    IDataAccess<CoreConnectorRecord> store,
    ISettingsProtector protector,
    AccessGrantEvaluator grants,
    TimeProvider clock)
{
    /// <summary>
    /// Creates a connector. A <see cref="ResourceScope.Personal"/> connector is owned by
    /// <paramref name="ownerPrincipalId"/> (identity-linked); company connectors carry no owner.
    /// </summary>
    public async Task<Connector> CreateAsync(CreateConnector request, Guid? ownerPrincipalId, CancellationToken ct)
    {
        var protectedSettings = request.Settings.ToDictionary(kv => kv.Key, kv => protector.Protect(kv.Value));
        var personal = request.Scope == ResourceScope.Personal;
        var record = new CoreConnectorRecord
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            ProviderType = request.ProviderType,
            ProtectedSettingsJson = JsonSerializer.Serialize(protectedSettings),
            Scope = personal ? ResourceScope.Personal : ResourceScope.Company,
            OwnerPrincipalId = personal ? ownerPrincipalId : null,
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(record, ct);
        return ToDto(record);
    }

    /// <summary>
    /// Updates a connector in place: rename and/or upsert settings key-by-key (each provided value
    /// is protected on write; untouched keys keep their stored value). This is how a rotated
    /// credential is refreshed without re-creating the connector or breaking its bindings.
    /// </summary>
    public async Task<Connector?> UpdateAsync(Guid id, UpdateConnector request, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } record)
            return null;
        var protectedSettings =
            JsonSerializer.Deserialize<Dictionary<string, string>>(record.ProtectedSettingsJson)
            ?? new Dictionary<string, string>();
        foreach (var (key, value) in request.Settings ?? new Dictionary<string, string>())
            protectedSettings[key] = protector.Protect(value);
        var updated = record with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? record.Name : request.Name,
            ProtectedSettingsJson = JsonSerializer.Serialize(protectedSettings),
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(updated, ct);
        return ToDto(updated);
    }

    /// <summary>Replaces a connector's access grants. Returns false when the connector is unknown.</summary>
    public async Task<bool> SetGrantsAsync(Guid id, IReadOnlyList<AccessGrant> grants, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } record)
            return false;
        await store.SaveAsync(
            record with { GrantsJson = JsonSerializer.Serialize(grants), UpdatedUtc = clock.GetUtcNow() }, ct);
        return true;
    }

    /// <summary>Unfiltered read — for Core-internal paths that already authorized the caller.</summary>
    public async Task<Connector?> GetAsync(Guid id, CancellationToken ct)
        => await store.ReadAsync(id, ct) is { } r ? ToDto(r) : null;

    /// <summary>Visibility-filtered read: an invisible connector reads as not found.</summary>
    public async Task<Connector?> GetAsync(Guid id, ConnectorViewer viewer, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } record)
            return null;
        return await IsVisibleAsync(record, viewer, ct) ? ToDto(record) : null;
    }

    public async Task<PagedResult<Connector>> QueryAsync(
        ConnectorQuery query, ConnectorViewer viewer, CancellationToken ct)
    {
        var all = (await store.ReadAsync(ct)).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.ProviderType))
            all = all.Where(c =>
                string.Equals(c.ProviderType, query.ProviderType, StringComparison.OrdinalIgnoreCase));

        var visible = new List<CoreConnectorRecord>();
        foreach (var record in all)
            if (await IsVisibleAsync(record, viewer, ct))
                visible.Add(record);

        var ordered = visible.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var page = ordered.Skip(query.Skip).Take(query.Take).Select(ToDto).ToList();
        return new PagedResult<Connector>(page, ordered.Count, query.Skip, query.Take);
    }

    private async Task<bool> IsVisibleAsync(
        CoreConnectorRecord record, ConnectorViewer viewer, CancellationToken ct)
    {
        if (record.Scope != ResourceScope.Personal || viewer.SeesAll)
            return true;
        if (viewer.PrincipalId is not { } pid)
            return false;
        if (record.OwnerPrincipalId == pid)
            return true;
        return await grants.IsGrantedAsync(record.GrantsJson, pid, ct);
    }

    /// <summary>
    /// Decrypts a connector's settings for just-in-time injection. Never call this from a read
    /// endpoint — the plaintext must not leave the Core except into a launching container.
    /// </summary>
    /// <summary>Deletes a connector permanently — configurations binding it will fail to dispatch.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        => await store.RemoveAsync(id, ct);

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
        var grants = JsonSerializer.Deserialize<List<AccessGrant>>(r.GrantsJson) ?? [];
        return new Connector(r.Id, r.Name, r.ProviderType, keys, r.UpdatedUtc, r.Scope, r.OwnerPrincipalId, grants);
    }
}

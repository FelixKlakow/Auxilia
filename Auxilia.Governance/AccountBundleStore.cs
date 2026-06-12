using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance;

/// <summary>An account bundle with its secret values stripped — only key names leave the store.</summary>
public sealed record AccountBundleSummary(
    Guid Id, string Name, string BundleType, Guid OwnerPrincipalId, IReadOnlyList<string> SecretKeys);

/// <summary>
/// Administration of account bundles (ARCHITECTURE §11). Secret values are write-only:
/// they are protected before persistence and only their key names are ever read back.
/// Every mutating operation is audited — without credential material.
/// </summary>
public sealed class AccountBundleStore(
    IDataAccess<AccountBundleRecord> bundles,
    ISettingsProtector protector,
    AuditLog auditLog)
{
    public async Task<IReadOnlyList<AccountBundleSummary>> ListAsync(CancellationToken ct = default)
    {
        var query = await bundles.ReadAsync(ct);
        return query.ToList().Select(ToSummary).OrderBy(b => b.Name).ToList();
    }

    public async Task<AccountBundleSummary> CreateAsync(
        string actor, string name, string bundleType, Guid ownerPrincipalId,
        IReadOnlyDictionary<string, string> secrets, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Bundle name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(bundleType))
            throw new ArgumentException("Bundle type is required.", nameof(bundleType));
        if (secrets.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Secret keys must be non-empty.", nameof(secrets));

        var record = new AccountBundleRecord
        {
            Id = AccountBundleRecord.IdFor(name.Trim()),
            Name = name.Trim(),
            BundleType = bundleType.Trim(),
            OwnerPrincipalId = ownerPrincipalId,
            ProtectedSecretsJson = ProtectSecrets(secrets.ToDictionary(s => s.Key, s => s.Value))
        };
        await bundles.SaveAsync(record, ct);
        await auditLog.AppendAsync(actor, "bundle.created", record.Id.ToString(), record.Name,
            JsonSerializer.Serialize(new { keys = secrets.Keys.Order().ToList() }), ct);
        return ToSummary(record);
    }

    /// <summary>Sets (adds or replaces) and removes secret keys; existing values are never returned.</summary>
    public async Task<AccountBundleSummary> UpdateSecretsAsync(
        string actor, Guid bundleId,
        IReadOnlyDictionary<string, string> setSecrets, IReadOnlyCollection<string> removeKeys,
        CancellationToken ct = default)
    {
        var record = await bundles.ReadAsync(bundleId, ct)
                     ?? throw new InvalidOperationException("Unknown account bundle.");
        if (setSecrets.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Secret keys must be non-empty.", nameof(setSecrets));

        var secrets = UnprotectSecrets(record.ProtectedSecretsJson);
        foreach (var key in removeKeys)
            secrets.Remove(key);
        foreach (var (key, value) in setSecrets)
            secrets[key] = value;

        var updated = record with { ProtectedSecretsJson = ProtectSecrets(secrets) };
        await bundles.SaveAsync(updated, ct);
        await auditLog.AppendAsync(actor, "bundle.updated", record.Id.ToString(), record.Name,
            JsonSerializer.Serialize(new
            {
                set = setSecrets.Keys.Order().ToList(),
                removed = removeKeys.Order().ToList()
            }), ct);
        return ToSummary(updated);
    }

    public async Task<bool> DeleteAsync(string actor, Guid bundleId, CancellationToken ct = default)
    {
        var record = await bundles.ReadAsync(bundleId, ct);
        if (record is null || !await bundles.RemoveAsync(bundleId, ct))
            return false;

        await auditLog.AppendAsync(actor, "bundle.deleted", bundleId.ToString(), record.Name, ct: ct);
        return true;
    }

    private string ProtectSecrets(Dictionary<string, string> secrets)
        => protector.Protect(JsonSerializer.Serialize(secrets));

    private Dictionary<string, string> UnprotectSecrets(string protectedJson)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(protectedJson)) ?? [];

    private AccountBundleSummary ToSummary(AccountBundleRecord record)
        => new(record.Id, record.Name, record.BundleType, record.OwnerPrincipalId,
            UnprotectSecrets(record.ProtectedSecretsJson).Keys.Order().ToList());
}

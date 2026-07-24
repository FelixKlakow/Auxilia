using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Runner.Workflows.Storage;

/// <summary>Decrypted in-memory view of a reusable slot instance.</summary>
public sealed record StoredSlotInstance(
    string Name,
    string DisplayName,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings,
    string Scope,
    Guid? OwnerPrincipalId,
    IReadOnlyList<Guid> AssignedPrincipalIds)
{
    public Guid Id => SlotInstanceRecord.IdFor(Name);
}

/// <summary>
/// Durable repository of reusable slot instances; one record per instance name. Settings are
/// run through the <see cref="ISettingsProtector"/> before persisting. Every mutation is
/// audited — without settings values.
/// </summary>
public sealed class SlotInstanceStore(
    IDataAccess<SlotInstanceRecord> dataAccess,
    ISettingsProtector protector,
    AuditLog auditLog,
    TimeProvider timeProvider)
{
    public async Task<StoredSlotInstance> UpsertAsync(StoredSlotInstance instance, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.Name))
            throw new ArgumentException("Slot instance name must not be empty.", nameof(instance));
        if (string.IsNullOrWhiteSpace(instance.ProviderType))
            throw new ArgumentException("Slot instance provider type must not be empty.", nameof(instance));
        if (instance.Scope is not (SlotInstanceScope.Company or SlotInstanceScope.Personal))
            throw new ArgumentException($"Unknown slot instance scope '{instance.Scope}'.", nameof(instance));

        var id = SlotInstanceRecord.IdFor(instance.Name);
        var existing = await dataAccess.ReadAsync(id, ct);
        var now = timeProvider.GetUtcNow();

        await dataAccess.SaveAsync(new SlotInstanceRecord
        {
            Id = id,
            Name = instance.Name,
            DisplayName = string.IsNullOrWhiteSpace(instance.DisplayName) ? instance.Name : instance.DisplayName,
            ProviderType = instance.ProviderType,
            ProtectedSettingsJson = protector.Protect(JsonSerializer.Serialize(instance.Settings)),
            Scope = instance.Scope,
            OwnerPrincipalId = instance.OwnerPrincipalId ?? existing?.OwnerPrincipalId,
            AssignedPrincipalIdsJson = JsonSerializer.Serialize(instance.AssignedPrincipalIds),
            CreatedUtc = existing?.CreatedUtc ?? now,
            UpdatedUtc = now
        }, ct);

        // Audited without settings values: only topology (name, provider, scope, assignees).
        await auditLog.AppendAsync(
            "steering-instance", "slot-instance.upserted",
            instance.Name, existing is null ? "created" : "updated",
            JsonSerializer.Serialize(new
            {
                providerType = instance.ProviderType,
                scope = instance.Scope,
                assignedPrincipals = instance.AssignedPrincipalIds
            }), ct);

        return (await GetAsync(id, ct))!;
    }

    public async Task<StoredSlotInstance?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(id, ct);
        return record is null ? null : FromRecord(record);
    }

    public async Task<IReadOnlyList<StoredSlotInstance>> GetAllAsync(CancellationToken ct = default)
    {
        var query = await dataAccess.ReadAsync(ct);
        return query.ToList().Select(FromRecord).ToList().AsReadOnly();
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        var removed = await dataAccess.RemoveAsync(SlotInstanceRecord.IdFor(name), ct);
        if (removed)
            await auditLog.AppendAsync(
                "steering-instance", "slot-instance.removed", name, "removed", ct: ct);
        return removed;
    }

    private StoredSlotInstance FromRecord(SlotInstanceRecord record)
        => new(
            record.Name,
            record.DisplayName,
            record.ProviderType,
            JsonSerializer.Deserialize<Dictionary<string, string>>(
                protector.Unprotect(record.ProtectedSettingsJson))!,
            record.Scope,
            record.OwnerPrincipalId,
            JsonSerializer.Deserialize<List<Guid>>(record.AssignedPrincipalIdsJson) ?? []);
}

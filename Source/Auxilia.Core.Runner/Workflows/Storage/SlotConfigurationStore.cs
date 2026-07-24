using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Runner.Workflows.Storage;

/// <summary>
/// Durable slot configuration repository; one record per (workflow type, slot name).
/// Settings are run through the <see cref="ISettingsProtector"/> before persisting,
/// so secrets are encrypted at rest whenever a protection key is configured.
/// </summary>
public sealed class SlotConfigurationStore(
    IDataAccess<SlotConfigurationRecord> dataAccess,
    ISettingsProtector protector)
{
    public async Task<IReadOnlyList<StoredSlotConfiguration>> GetConfigurationsAsync(
        string workflowTypeName, CancellationToken ct = default)
    {
        var query = await dataAccess.ReadAsync(ct);
        return query
            .Where(r => r.WorkflowType == workflowTypeName)
            .ToList()
            .Select(FromRecord)
            .ToList()
            .AsReadOnly();
    }

    public Task UpsertConfigurationAsync(
        string workflowTypeName, StoredSlotConfiguration config, CancellationToken ct = default)
        => dataAccess.SaveAsync(ToRecord(workflowTypeName, config), ct);

    public Task<bool> RemoveConfigurationAsync(
        string workflowTypeName, string slotName, CancellationToken ct = default)
        => dataAccess.RemoveAsync(SlotConfigurationRecord.IdFor(workflowTypeName, slotName), ct);

    public async Task MarkDirtyAsync(string workflowTypeName, CancellationToken ct = default)
    {
        var query = await dataAccess.ReadAsync(ct);
        var records = query.Where(r => r.WorkflowType == workflowTypeName).ToList();
        foreach (var record in records)
            await dataAccess.SaveAsync(record with { Status = nameof(ConfigurationStatus.Dirty) }, ct);
    }

    private SlotConfigurationRecord ToRecord(string workflowTypeName, StoredSlotConfiguration config) => new()
    {
        Id = SlotConfigurationRecord.IdFor(workflowTypeName, config.SlotName),
        WorkflowType = workflowTypeName,
        SlotName = config.SlotName,
        ProviderType = config.ProviderType,
        ProtectedSettingsJson = protector.Protect(JsonSerializer.Serialize(config.Settings)),
        Status = config.Status.ToString()
    };

    private StoredSlotConfiguration FromRecord(SlotConfigurationRecord record) => new(
        record.SlotName,
        record.ProviderType,
        JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(record.ProtectedSettingsJson))!,
        Enum.Parse<ConfigurationStatus>(record.Status));
}

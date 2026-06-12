using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;

namespace Auxilia.SteeringInstance.Workflows.Storage;

/// <summary>Durable slot-provider plugin registry; one record per provider type.</summary>
public sealed class SlotProviderRegistry(IDataAccess<SlotProviderRecord> dataAccess)
{
    public Task UpsertAsync(
        string providerType, string dllPath,
        IReadOnlyList<SettingDescriptor>? settings = null, CancellationToken ct = default)
        => dataAccess.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor(providerType),
            ProviderType = providerType,
            DllPath = dllPath,
            SettingDescriptorsJson = settings is { Count: > 0 } ? JsonSerializer.Serialize(settings) : null
        }, ct);

    public async Task<string?> GetDllPathAsync(string providerType, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(SlotProviderRecord.IdFor(providerType), ct);
        return record?.DllPath;
    }

    public Task<bool> RemoveAsync(string providerType, CancellationToken ct = default)
        => dataAccess.RemoveAsync(SlotProviderRecord.IdFor(providerType), ct);
}

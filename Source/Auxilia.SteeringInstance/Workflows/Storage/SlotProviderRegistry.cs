using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.SteeringInstance.Workflows.Storage;

/// <summary>Durable slot-provider plugin registry; one record per provider type.</summary>
public sealed class SlotProviderRegistry(IDataAccess<SlotProviderRecord> dataAccess)
{
    public Task UpsertAsync(string providerType, string dllPath, CancellationToken ct = default)
        => dataAccess.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor(providerType),
            ProviderType = providerType,
            DllPath = dllPath
        }, ct);

    public async Task<string?> GetDllPathAsync(string providerType, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(SlotProviderRecord.IdFor(providerType), ct);
        return record?.DllPath;
    }

    public Task<bool> RemoveAsync(string providerType, CancellationToken ct = default)
        => dataAccess.RemoveAsync(SlotProviderRecord.IdFor(providerType), ct);
}

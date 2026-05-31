using System.Collections.Concurrent;

namespace Auxilia.SteeringInstance.Workflows.Storage;

public sealed class SlotProviderRegistry
{
    private readonly ConcurrentDictionary<string, string> _store = new();

    public void Upsert(string providerType, string dllPath)
        => _store[providerType] = dllPath;

    public bool TryGet(string providerType, out string dllPath)
        => _store.TryGetValue(providerType, out dllPath!);

    public void Remove(string providerType)
        => _store.TryRemove(providerType, out _);
}

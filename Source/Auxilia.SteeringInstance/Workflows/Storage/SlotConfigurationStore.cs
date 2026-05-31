using System.Collections.Concurrent;

namespace Auxilia.SteeringInstance.Workflows.Storage;

public sealed class SlotConfigurationStore
{
    private readonly ConcurrentDictionary<string, List<StoredSlotConfiguration>> _store = new();

    public IReadOnlyList<StoredSlotConfiguration> GetConfigurations(string workflowTypeName)
    {
        return _store.TryGetValue(workflowTypeName, out var list)
            ? list.AsReadOnly()
            : [];
    }

    public void UpsertConfiguration(string workflowTypeName, StoredSlotConfiguration config)
    {
        _store.AddOrUpdate(
            workflowTypeName,
            _ => [config],
            (_, existing) =>
            {
                lock (existing)
                {
                    var index = existing.FindIndex(c => c.SlotName == config.SlotName);
                    if (index >= 0)
                        existing[index] = config;
                    else
                        existing.Add(config);
                    return existing;
                }
            });
    }

    public void RemoveConfiguration(string workflowTypeName, string slotName)
    {
        _store.AddOrUpdate(
            workflowTypeName,
            _ => [],
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.RemoveAll(c => c.SlotName == slotName);
                    return existing;
                }
            });
    }

    public void MarkDirty(string workflowTypeName)
    {
        _store.AddOrUpdate(
            workflowTypeName,
            _ => [],
            (_, existing) =>
            {
                lock (existing)
                {
                    for (var i = 0; i < existing.Count; i++)
                        existing[i] = existing[i] with { Status = ConfigurationStatus.Dirty };
                    return existing;
                }
            });
    }
}

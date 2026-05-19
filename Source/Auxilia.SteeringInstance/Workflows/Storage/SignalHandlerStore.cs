using System.Collections.Concurrent;

namespace Auxilia.SteeringInstance.Workflows.Storage;

public sealed class SignalHandlerStore
{
    private readonly ConcurrentDictionary<string, List<StoredSignalHandlerConfiguration>> _store = new();

    public IReadOnlyList<StoredSignalHandlerConfiguration> GetHandlers(string workflowTypeName)
    {
        return _store.TryGetValue(workflowTypeName, out var list)
            ? list.AsReadOnly()
            : [];
    }

    public void UpsertHandler(string workflowTypeName, StoredSignalHandlerConfiguration config)
    {
        _store.AddOrUpdate(
            workflowTypeName,
            _ => [config],
            (_, existing) =>
            {
                lock (existing)
                {
                    var index = existing.FindIndex(c => c.SignalName == config.SignalName);
                    if (index >= 0)
                        existing[index] = config;
                    else
                        existing.Add(config);
                    return existing;
                }
            });
    }

    public void MarkDirty(string workflowTypeName)
    {
        // Reserved for future schema invalidation; no-op equivalent (no Status field on the record).
        _store.AddOrUpdate(
            workflowTypeName,
            _ => [],
            (_, existing) => existing);
    }
}

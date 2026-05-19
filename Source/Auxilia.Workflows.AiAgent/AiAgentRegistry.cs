using System.Collections.Concurrent;
using Auxilia.AI;

namespace Auxilia.Workflows.AiAgent;

public sealed class AiAgentRegistry : IAiAgentRegistry
{
    private readonly ConcurrentDictionary<string, IAgentSessionBuilder> _prototypes = new();

    public void RegisterPrototype(string slotName, IAgentSessionBuilder prototype)
    {
        if (!_prototypes.TryAdd(slotName, prototype))
            throw new ArgumentException($"A prototype for slot '{slotName}' is already registered.", nameof(slotName));
    }

    public Task<IAgentSession> CreateSessionAsync(string slotName)
    {
        if (!_prototypes.TryGetValue(slotName, out var builder))
            throw new KeyNotFoundException($"No prototype registered for slot '{slotName}'.");
        return builder.BuildAsync();
    }
}

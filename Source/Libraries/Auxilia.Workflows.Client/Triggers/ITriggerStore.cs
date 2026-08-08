using System.Collections.Concurrent;

namespace Auxilia.Workflows.Client.Triggers;

/// <summary>
/// Host-pluggable persistence for trigger definitions. The library ships an in-memory default;
/// a host that wants triggers to survive restarts implements this over its own storage
/// (a file, a database, user settings — the host's choice, never the Core's).
/// </summary>
public interface ITriggerStore
{
    Task<IReadOnlyList<ScheduledTriggerDefinition>> GetScheduledTriggersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ArtifactTriggerDefinition>> GetArtifactTriggersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventTriggerDefinition>> GetEventTriggersAsync(CancellationToken ct = default);
    Task SaveAsync(ScheduledTriggerDefinition trigger, CancellationToken ct = default);
    Task SaveAsync(ArtifactTriggerDefinition trigger, CancellationToken ct = default);
    Task SaveAsync(EventTriggerDefinition trigger, CancellationToken ct = default);
    Task DeleteScheduledTriggerAsync(Guid id, CancellationToken ct = default);
    Task DeleteArtifactTriggerAsync(Guid id, CancellationToken ct = default);
    Task DeleteEventTriggerAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Default store: triggers live for the host's lifetime.</summary>
public sealed class InMemoryTriggerStore : ITriggerStore
{
    private readonly ConcurrentDictionary<Guid, ScheduledTriggerDefinition> _scheduled = new();
    private readonly ConcurrentDictionary<Guid, ArtifactTriggerDefinition> _artifact = new();
    private readonly ConcurrentDictionary<Guid, EventTriggerDefinition> _event = new();

    public Task<IReadOnlyList<ScheduledTriggerDefinition>> GetScheduledTriggersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ScheduledTriggerDefinition>>(_scheduled.Values.ToList());

    public Task<IReadOnlyList<ArtifactTriggerDefinition>> GetArtifactTriggersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ArtifactTriggerDefinition>>(_artifact.Values.ToList());

    public Task<IReadOnlyList<EventTriggerDefinition>> GetEventTriggersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EventTriggerDefinition>>(_event.Values.ToList());

    public Task SaveAsync(ScheduledTriggerDefinition trigger, CancellationToken ct = default)
    {
        _scheduled[trigger.Id] = trigger;
        return Task.CompletedTask;
    }

    public Task SaveAsync(ArtifactTriggerDefinition trigger, CancellationToken ct = default)
    {
        _artifact[trigger.Id] = trigger;
        return Task.CompletedTask;
    }

    public Task SaveAsync(EventTriggerDefinition trigger, CancellationToken ct = default)
    {
        _event[trigger.Id] = trigger;
        return Task.CompletedTask;
    }

    public Task DeleteScheduledTriggerAsync(Guid id, CancellationToken ct = default)
    {
        _scheduled.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteArtifactTriggerAsync(Guid id, CancellationToken ct = default)
    {
        _artifact.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteEventTriggerAsync(Guid id, CancellationToken ct = default)
    {
        _event.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

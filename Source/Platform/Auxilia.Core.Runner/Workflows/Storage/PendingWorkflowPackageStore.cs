using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Auxilia.Core.Runner.Workflows.Storage;

/// <summary>
/// Holds the extracted filesystem path for a downloaded workflow package until the
/// announcement handler consumes it. Keyed by the run's instance id — two concurrent
/// dispatches of one type never see each other's package. Each entry is consumed exactly once.
/// </summary>
public class PendingWorkflowPackageStore
{
    private readonly ConcurrentDictionary<Guid, string> _store = new();

    /// <summary>Registers an extracted package path for the given run.</summary>
    public virtual void Store(Guid instanceId, string extractedPath)
        => _store[instanceId] = extractedPath;

    /// <summary>
    /// Attempts to retrieve and remove the pending extracted path for <paramref name="instanceId"/>.
    /// Returns <see langword="true"/> and sets <paramref name="extractedPath"/> when found.
    /// </summary>
    public virtual bool TryConsume(Guid instanceId, [NotNullWhen(true)] out string? extractedPath)
        => _store.TryRemove(instanceId, out extractedPath);
}

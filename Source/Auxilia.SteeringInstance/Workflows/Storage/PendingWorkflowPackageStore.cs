using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Auxilia.SteeringInstance.Workflows.Storage;

/// <summary>
/// Holds the extracted filesystem path for a downloaded workflow package until
/// the announcement handler consumes it.  Each entry is consumed exactly once.
/// </summary>
public class PendingWorkflowPackageStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers an extracted package path for the given workflow type.</summary>
    public virtual void Store(string workflowType, string extractedPath)
        => _store[workflowType] = extractedPath;

    /// <summary>
    /// Attempts to retrieve and remove the pending extracted path for <paramref name="workflowType"/>.
    /// Returns <see langword="true"/> and sets <paramref name="extractedPath"/> when found.
    /// </summary>
    public virtual bool TryConsume(string workflowType, [NotNullWhen(true)] out string? extractedPath)
        => _store.TryRemove(workflowType, out extractedPath);
}

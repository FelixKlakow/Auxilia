namespace Auxilia.Workflows.TaskSource;

/// <summary>
/// Task/work-item interface injected into workflows.
/// Provider packages implement this interface. Workflow code depends on it.
/// </summary>
public interface ITaskSource
{
    /// <summary>Retrieves a single work item by its provider-native identifier, or <c>null</c> if not found.</summary>
    Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Retrieves multiple work items by their provider-native identifiers. Items not found are omitted from the result.</summary>
    Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);
}

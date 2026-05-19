namespace Auxilia.Workflows.TaskSource;

/// <summary>
/// Behavioral contract for work-item access inside a workflow.
/// Replaces the <see cref="ITaskSource"/> placeholder.
/// </summary>
public interface IWorkItemAccess
{
    Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default);
}

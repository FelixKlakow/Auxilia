namespace Auxilia.Workflows.TaskSource;

public interface ITaskSourceAccess
{
    Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);
    Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(string id, string newStatus, CancellationToken cancellationToken = default);
}

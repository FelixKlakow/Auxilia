namespace Auxilia.Workflows.TaskSource;

/// <summary>One file attached to a work item (e.g. a mail attachment); content, never a link.</summary>
public sealed record WorkItemAttachment(string FileName, byte[] Content);

/// <summary>
/// Behavioral contract for work-item access inside a workflow.
/// Replaces the <see cref="ITaskSource"/> placeholder.
/// </summary>
public interface IWorkItemAccess
{
    Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default);

    /// <summary>The work item's attachments; providers without attachment support return none.</summary>
    Task<IReadOnlyList<WorkItemAttachment>> GetAttachmentsAsync(
        string id, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkItemAttachment>>([]);
}

namespace Auxilia.Workflows.TaskSource;

/// <summary>One file attached to a work item (e.g. a mail attachment); content, never a link.</summary>
public sealed record WorkItemAttachment(string FileName, byte[] Content);

/// <summary>
/// One relation of a work item: parent/child/related work items (with their id and, best
/// effort, title) or plain hyperlinks (kind "link", the URL in <see cref="Url"/>). Kinds are
/// an open, source-mapped vocabulary — never a compiled enum.
/// </summary>
public sealed record WorkItemRelation(string Kind, string TargetId, string? Title, string? Url);

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

    /// <summary>
    /// The state vocabulary of THIS work item's type at the source (e.g. New/Active/Testing/
    /// Done for an AzDO user story) — never a hardcoded platform list. Providers without a
    /// state model (mail) return none, and state steps skip.
    /// </summary>
    Task<IReadOnlyList<string>> GetStatesAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Sets the work item's state to one of <see cref="GetStatesAsync"/>'s values.</summary>
    Task SetStateAsync(string id, string state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This work-item source has no state model.");

    /// <summary>
    /// The work item's relations — parents, children, related items, hyperlinks — in the
    /// source's own link vocabulary. Providers without a link model return none.
    /// </summary>
    Task<IReadOnlyList<WorkItemRelation>> GetRelationsAsync(
        string id, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkItemRelation>>([]);
}

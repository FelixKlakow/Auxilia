using Auxilia.Adapters.Email;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.Slots.Email;

/// <summary>
/// Work-item access over a mailbox: the triggering mail (exposed via the
/// <c>WORKFLOW_CONTEXT__*</c> launch context) is the work item; comments are sent as replies.
/// </summary>
internal sealed class EmailWorkItemAccess(IMailboxClient mailbox) : IWorkItemAccess
{
    internal const string WorkItemIdVariable = "WORKFLOW_CONTEXT__WORKITEMID";
    internal const string TitleVariable = "WORKFLOW_CONTEXT__TITLE";
    internal const string FromVariable = "WORKFLOW_CONTEXT__FROM";
    internal const string BodyVariable = "WORKFLOW_CONTEXT__BODY";

    public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
    {
        var workItemId = Environment.GetEnvironmentVariable(WorkItemIdVariable) ?? id;
        var title = Environment.GetEnvironmentVariable(TitleVariable) ?? string.Empty;
        var body = Environment.GetEnvironmentVariable(BodyVariable);
        return Task.FromResult<WorkItem?>(new WorkItem(workItemId, title, body, null, null, []));
    }

    public async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var items = new List<WorkItem>();
        foreach (var id in ids)
            if (await GetWorkItemAsync(id, cancellationToken) is { } item)
                items.Add(item);
        return items;
    }

    /// <summary>Refetches the triggering mail by its protocol UID and returns its attachments.</summary>
    public async Task<IReadOnlyList<WorkItemAttachment>> GetAttachmentsAsync(
        string id, CancellationToken cancellationToken = default)
    {
        var uidRaw = Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__MAILUID");
        if (!uint.TryParse(uidRaw, out var uid))
            return [];
        return (await mailbox.FetchAttachmentsAsync(uid, cancellationToken))
            .Select(a => new WorkItemAttachment(a.FileName, a.Content))
            .ToList();
    }

    public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
    {
        var to = Environment.GetEnvironmentVariable(FromVariable)
                 ?? throw new InvalidOperationException(
                     $"{FromVariable} is not set; cannot determine the reply recipient.");
        var title = Environment.GetEnvironmentVariable(TitleVariable) ?? string.Empty;
        var workItemId = Environment.GetEnvironmentVariable(WorkItemIdVariable) ?? id;

        // No Message-Id is carried in the launch context, so the reply goes out without an
        // In-Reply-To header; the work-item marker in the body keeps the thread traceable.
        var body = $"{comment}\n\n[work-item: {workItemId}]";
        return mailbox.SendReplyAsync(to, $"Re: {title}", body, inReplyToMessageId: string.Empty, cancellationToken);
    }
}

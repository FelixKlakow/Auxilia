using Auxilia.Workflows.TaskSource;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeTaskSourceAccess : ITaskSourceAccess
{
    public Dictionary<string, WorkItem> WorkItems { get; } = new();
    public List<(string Id, string Comment)> PostedComments { get; } = new();
    public List<(string Id, string Status)> StatusUpdates { get; } = new();
    public bool PostCommentThrows { get; set; }
    public bool UpdateStatusThrows { get; set; }

    public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
    {
        WorkItems.TryGetValue(id, out var item);
        return Task.FromResult<WorkItem?>(item);
    }

    public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WorkItem> result = ids
            .Where(WorkItems.ContainsKey)
            .Select(id => WorkItems[id])
            .ToList();
        return Task.FromResult(result);
    }

    public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
    {
        if (PostCommentThrows)
            throw new InvalidOperationException("Scripted write-back failure.");

        PostedComments.Add((id, comment));
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(string id, string newStatus, CancellationToken cancellationToken = default)
    {
        if (UpdateStatusThrows)
            throw new InvalidOperationException("Scripted status-update failure.");

        StatusUpdates.Add((id, newStatus));
        return Task.CompletedTask;
    }
}

using Auxilia.Workflows.TaskSource;

namespace Auxilia.Workflows.SlotPackages.Tests.TestDoubles;

public sealed class StubWorkItemAccess : IWorkItemAccess
{
    public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<WorkItem?>(new WorkItem(
            id,
            $"Stub work item {id}",
            "This is a stub description.",
            "Active",
            "stub-user",
            ["backend", "urgent"]));

    public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var items = ids.Select(id => new WorkItem(
            id,
            $"Stub work item {id}",
            "This is a stub description.",
            "Active",
            "stub-user",
            ["backend"])).ToList();

        return Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

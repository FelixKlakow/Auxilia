using Auxilia.Workflows.TaskSource;

namespace Auxilia.Workflows.SlotPackages.Tests.TestDoubles;

/// <summary>Stub for <see cref="IWorkItemAccess"/> that always returns <c>null</c> for single-item lookups.</summary>
public sealed class NullStubWorkItemAccess : IWorkItemAccess
{
    public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<WorkItem?>(null);

    public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkItem>>([]);

    public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

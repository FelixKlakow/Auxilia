using Auxilia.Workflows.TaskSource;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeWorkItemAccess : IWorkItemAccess
{
    private readonly IReadOnlyDictionary<string, WorkItem> _items;
    private readonly Exception? _failOnLookup;
    private readonly Exception? _throwOnPost;

    public FakeWorkItemAccess(
        IReadOnlyDictionary<string, WorkItem>? items = null,
        Exception? failOnLookup = null,
        Exception? throwOnPost = null)
    {
        _items = items ?? new Dictionary<string, WorkItem>();
        _failOnLookup = failOnLookup;
        _throwOnPost = throwOnPost;
    }

    public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_failOnLookup is not null)
            throw _failOnLookup;

        _items.TryGetValue(id, out var item);
        return Task.FromResult(item);
    }

    public async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var results = new List<WorkItem>();
        foreach (var id in ids)
        {
            var item = await GetWorkItemAsync(id, cancellationToken);
            if (item is not null)
                results.Add(item);
        }
        return results;
    }

    public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
    {
        if (_throwOnPost is not null)
            throw _throwOnPost;

        return Task.CompletedTask;
    }
}

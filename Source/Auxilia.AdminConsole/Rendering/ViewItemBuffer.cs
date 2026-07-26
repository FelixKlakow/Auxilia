namespace Auxilia.AdminConsole.Rendering;

/// <summary>
/// Per-view item list keyed by the view's monotonic sequence: live SSE pushes and persisted-store
/// reads merge into one ordered list without duplicates or regressions.
/// </summary>
public sealed class ViewItemBuffer
{
    private readonly object _gate = new();
    private readonly SortedDictionary<long, ViewDataRecord> _items = new();

    public IReadOnlyList<ViewDataRecord> Items
    {
        get
        {
            lock (_gate)
                return _items.Values.ToList();
        }
    }

    /// <summary>Adds one item; returns false when its sequence is already present.</summary>
    public bool Add(ViewDataRecord item)
    {
        lock (_gate)
            return _items.TryAdd(item.Sequence, item);
    }

    /// <summary>Merges a store read; returns true when anything new was added.</summary>
    public bool Merge(IEnumerable<ViewDataRecord> items)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var item in items)
                changed |= _items.TryAdd(item.Sequence, item);
            return changed;
        }
    }
}

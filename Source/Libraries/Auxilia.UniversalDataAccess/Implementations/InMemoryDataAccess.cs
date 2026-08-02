using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Auxilia.UniversalDataAccess.Implementations;

public sealed class InMemoryDataAccess<TEntity> : IDataAccess<TEntity>, IDisposable where TEntity : IEntity
{
    private readonly Dictionary<Guid, TEntity> _store = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    private readonly Subject<TEntity> _entityAdded = new();
    private readonly Subject<TEntity> _entityUpdated = new();
    private readonly Subject<TEntity> _entityRemoved = new();

    public IObservable<TEntity> EntityAdded => _entityAdded.AsObservable();
    public IObservable<TEntity> EntityUpdated => _entityUpdated.AsObservable();
    public IObservable<TEntity> EntityRemoved => _entityRemoved.AsObservable();

    public Task<IQueryable<TEntity>> ReadAsync() => ReadAsync(CancellationToken.None);

    public Task<IQueryable<TEntity>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (_lock.Read())
        {
            return Task.FromResult(_store.Values.ToList().AsQueryable());
        }
    }

    public Task<TEntity?> ReadAsync(Guid id) => ReadAsync(id, CancellationToken.None);

    public Task<TEntity?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (_lock.Read())
        {
            _store.TryGetValue(id, out var entity);
            return Task.FromResult(entity);
        }
    }

    public Task<bool> SaveAsync(TEntity entity) => SaveAsync(entity, CancellationToken.None);

    public Task<bool> SaveAsync(TEntity entity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool updated;
        using (_lock.Write())
        {
            updated = _store.ContainsKey(entity.Id);
            _store[entity.Id] = entity;
        }

        if (updated)
            _entityUpdated.OnNext(entity);
        else
            _entityAdded.OnNext(entity);

        return Task.FromResult(updated);
    }

    public Task<bool> RemoveAsync(Guid id) => RemoveAsync(id, CancellationToken.None);

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TEntity? removed = default;
        using (_lock.Write())
        {
            if (_store.TryGetValue(id, out removed))
                _store.Remove(id);
        }

        if (removed is not null)
        {
            _entityRemoved.OnNext(removed);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
        _entityAdded.Dispose();
        _entityUpdated.Dispose();
        _entityRemoved.Dispose();
    }
}








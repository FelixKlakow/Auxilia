using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.EntityFrameworkCore;

namespace Auxilia.UniversalDataAccess.Implementations;

/// <summary>
/// <see cref="IDataAccess{TEntity}"/> backed by MongoDB via EF Core.
/// Each operation opens a short-lived <see cref="MongoDbContext{TEntity}"/> through the
/// injected factory, keeping the implementation stateless with respect to the database
/// while the observable subjects provide the reactive notification layer.
/// </summary>
public sealed class MongoDbEfDataAccess<TEntity> : IDataAccess<TEntity>, IDisposable
    where TEntity : class, IEntity
{
    private readonly IDbContextFactory<MongoDbContext<TEntity>> _contextFactory;

    private readonly Subject<TEntity> _entityAdded = new();
    private readonly Subject<TEntity> _entityUpdated = new();
    private readonly Subject<TEntity> _entityRemoved = new();
    private bool _disposed;

    public IObservable<TEntity> EntityAdded => _entityAdded.AsObservable();
    public IObservable<TEntity> EntityUpdated => _entityUpdated.AsObservable();
    public IObservable<TEntity> EntityRemoved => _entityRemoved.AsObservable();

    public MongoDbEfDataAccess(IDbContextFactory<MongoDbContext<TEntity>> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    // ── Read ────────────────────────────────────────────────────────────────

    public Task<IQueryable<TEntity>> ReadAsync() => ReadAsync(CancellationToken.None);

    public async Task<IQueryable<TEntity>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Materialise the list before the context is disposed
        var list = await ctx.Entities.ToListAsync(cancellationToken);
        return list.AsQueryable();
    }

    public Task<TEntity?> ReadAsync(Guid id) => ReadAsync(id, CancellationToken.None);

    public async Task<TEntity?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Entities.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    // ── Save ────────────────────────────────────────────────────────────────

    public Task<bool> SaveAsync(TEntity entity) => SaveAsync(entity, CancellationToken.None);

    public async Task<bool> SaveAsync(TEntity entity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // FindAsync uses the primary-key cache before hitting the DB
        var existing = await ctx.Entities.FindAsync([entity.Id], cancellationToken);
        var updated = existing is not null;

        if (updated)
        {
            // Detach the DB copy so we can attach the caller's instance as Modified
            ctx.Entry(existing!).State = EntityState.Detached;
            ctx.Entry(entity).State = EntityState.Modified;
        }
        else
        {
            await ctx.Entities.AddAsync(entity, cancellationToken);
        }

        await ctx.SaveChangesAsync(cancellationToken);

        if (updated)
            _entityUpdated.OnNext(entity);
        else
            _entityAdded.OnNext(entity);

        return updated;
    }

    // ── Remove ──────────────────────────────────────────────────────────────

    public Task<bool> RemoveAsync(Guid id) => RemoveAsync(id, CancellationToken.None);

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await ctx.Entities.FindAsync([id], cancellationToken);
        if (existing is null) return false;

        ctx.Entities.Remove(existing);
        await ctx.SaveChangesAsync(cancellationToken);

        _entityRemoved.OnNext(existing);
        return true;
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _entityAdded.Dispose();
        _entityUpdated.Dispose();
        _entityRemoved.Dispose();
    }
}


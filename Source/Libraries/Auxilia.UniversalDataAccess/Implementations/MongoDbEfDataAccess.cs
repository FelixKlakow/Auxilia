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
        if (entity is IVersionedEntity versioned)
            return await SaveVersionedAsync(entity, versioned, cancellationToken);

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

    /// <summary>
    /// Last-writer-wins save that still advances the version atomically: the Version concurrency
    /// token (see <see cref="MongoDbContext{TEntity}"/>) turns each write into a filtered replace
    /// against the version just read, and a lost race simply re-reads and re-applies — the caller's
    /// values win, but every write bumps the stored version so conditional saves observe it.
    /// </summary>
    private async Task<bool> SaveVersionedAsync(
        TEntity entity, IVersionedEntity versioned, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var existing = await ctx.Entities.FindAsync([entity.Id], cancellationToken);
            var updated = existing is not null;
            try
            {
                if (updated)
                {
                    var storedVersion = ((IVersionedEntity)existing!).Version;
                    ctx.Entry(existing!).State = EntityState.Detached;
                    versioned.Version = storedVersion + 1;
                    var entry = ctx.Entry(entity);
                    entry.State = EntityState.Modified;
                    entry.Property(nameof(IVersionedEntity.Version)).OriginalValue = storedVersion;
                }
                else
                {
                    versioned.Version = 1;
                    await ctx.Entities.AddAsync(entity, cancellationToken);
                }

                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxWriteAttempts)
            {
                // A concurrent writer moved the version — last writer wins: re-read the fresh
                // version and re-apply this entity's values. Bounded, so a store that keeps
                // rejecting the write surfaces the exception instead of spinning forever.
                await BackoffAsync(attempt, cancellationToken);
                continue;
            }
            catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && !updated && attempt < MaxWriteAttempts)
            {
                // Insert path: only a concurrent writer inserting the same id first is a race worth
                // retrying (as an update). Every other write error is real and must surface.
                await using var probe = await _contextFactory.CreateDbContextAsync(cancellationToken);
                if (!await probe.Entities.AnyAsync(e => e.Id == entity.Id, cancellationToken))
                    throw;
                await BackoffAsync(attempt, cancellationToken);
                continue;
            }

            if (updated)
                _entityUpdated.OnNext(entity);
            else
                _entityAdded.OnNext(entity);

            return updated;
        }
    }

    /// <summary>Upper bound on optimistic-write retries before the concurrency exception surfaces.</summary>
    private const int MaxWriteAttempts = 8;

    private static Task BackoffAsync(int attempt, CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1, 5 * attempt)), cancellationToken);

    public Task<bool> TrySaveAsync(TEntity entity, long expectedVersion) => TrySaveAsync(entity, expectedVersion, CancellationToken.None);

    public async Task<bool> TrySaveAsync(TEntity entity, long expectedVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entity is not IVersionedEntity versioned)
            throw new NotSupportedException(
                $"{typeof(TEntity).Name} does not implement {nameof(IVersionedEntity)} — conditional saves need a version.");

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ctx.Entities.FindAsync([entity.Id], cancellationToken);

        if (existing is null)
        {
            if (expectedVersion != 0)
                return false;
            versioned.Version = 1;
            await ctx.Entities.AddAsync(entity, cancellationToken);
            try
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return false; // a concurrent writer inserted first — the swap is lost
            }

            _entityAdded.OnNext(entity);
            return true;
        }

        if (((IVersionedEntity)existing).Version != expectedVersion)
            return false;

        // The Version concurrency token makes this a filtered replace: it only lands if the
        // stored document still carries expectedVersion (a concurrent delete also loses).
        ctx.Entry(existing).State = EntityState.Detached;
        versioned.Version = expectedVersion + 1;
        var entry = ctx.Entry(entity);
        entry.State = EntityState.Modified;
        entry.Property(nameof(IVersionedEntity.Version)).OriginalValue = expectedVersion;
        try
        {
            await ctx.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }

        _entityUpdated.OnNext(entity);
        return true;
    }

    // ── Remove ──────────────────────────────────────────────────────────────

    public Task<bool> RemoveAsync(Guid id) => RemoveAsync(id, CancellationToken.None);

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var existing = await ctx.Entities.FindAsync([id], cancellationToken);
            if (existing is null) return false;

            ctx.Entities.Remove(existing);
            try
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxWriteAttempts)
            {
                // Versioned entities delete through the Version token: a concurrent writer moved
                // the version (re-read and delete again) or already removed it (report false).
                await BackoffAsync(attempt, cancellationToken);
                continue;
            }

            _entityRemoved.OnNext(existing);
            return true;
        }
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


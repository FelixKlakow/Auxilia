using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Auxilia.UniversalDataAccess.Settings;

namespace Auxilia.UniversalDataAccess.Implementations;

public sealed class JsonFileDataAccess<TEntity> : IDataAccess<TEntity>, IDisposable where TEntity : IEntity
{
    private readonly JsonStorageSettings<TEntity> _settings;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<Guid, TEntity> _cache = new();
    private bool _cacheLoaded;
    private bool _dirty;
    private bool _disposed;

    private readonly Subject<TEntity> _entityAdded = new();
    private readonly Subject<TEntity> _entityUpdated = new();
    private readonly Subject<TEntity> _entityRemoved = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public IObservable<TEntity> EntityAdded => _entityAdded.AsObservable();
    public IObservable<TEntity> EntityUpdated => _entityUpdated.AsObservable();
    public IObservable<TEntity> EntityRemoved => _entityRemoved.AsObservable();

    public JsonFileDataAccess(JsonStorageSettings<TEntity> settings)
    {
        _settings = settings;
    }

    private void EnsureCacheLoaded()
    {
        // Must be called within a write lock
        if (_cacheLoaded) return;

        if (File.Exists(_settings.StorageFilePath))
        {
            var json = File.ReadAllText(_settings.StorageFilePath);
            var entities = JsonSerializer.Deserialize<List<TEntity>>(json, JsonOptions) ?? [];
            foreach (var entity in entities)
                _cache[entity.Id] = entity;
        }

        _cacheLoaded = true;
    }

    private void PersistIfRequired()
    {
        // Must be called within a write lock
        if (_settings.OnlySaveOnDispose) return;
        PersistToFile();
    }

    private void PersistToFile()
    {
        // Must be called within a write lock (or at dispose time)
        var dir = Path.GetDirectoryName(_settings.StorageFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_cache.Values.ToList(), JsonOptions);
        File.WriteAllText(_settings.StorageFilePath, json);
        _dirty = false;
    }

    public Task<IQueryable<TEntity>> ReadAsync() => ReadAsync(CancellationToken.None);

    public Task<IQueryable<TEntity>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (_lock.Write()) // write lock needed for EnsureCacheLoaded
        {
            EnsureCacheLoaded();
            return Task.FromResult(_cache.Values.ToList().AsQueryable());
        }
    }

    public Task<TEntity?> ReadAsync(Guid id) => ReadAsync(id, CancellationToken.None);

    public Task<TEntity?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (_lock.Write()) // write lock needed for EnsureCacheLoaded
        {
            EnsureCacheLoaded();
            _cache.TryGetValue(id, out var entity);
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
            EnsureCacheLoaded();
            updated = _cache.TryGetValue(entity.Id, out var current);
            if (entity is IVersionedEntity versioned)
                versioned.Version = (current is IVersionedEntity stored ? stored.Version : 0) + 1;
            _cache[entity.Id] = entity;
            _dirty = true;
            PersistIfRequired();
        }

        if (updated)
            _entityUpdated.OnNext(entity);
        else
            _entityAdded.OnNext(entity);

        return Task.FromResult(updated);
    }

    public Task<bool> TrySaveAsync(TEntity entity, long expectedVersion) => TrySaveAsync(entity, expectedVersion, CancellationToken.None);

    public Task<bool> TrySaveAsync(TEntity entity, long expectedVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entity is not IVersionedEntity versioned)
            throw new NotSupportedException(
                $"{typeof(TEntity).Name} does not implement {nameof(IVersionedEntity)} — conditional saves need a version.");
        bool updated;
        using (_lock.Write())
        {
            EnsureCacheLoaded();
            updated = _cache.TryGetValue(entity.Id, out var current);
            var currentVersion = current is IVersionedEntity stored ? stored.Version : 0;
            if (currentVersion != expectedVersion)
                return Task.FromResult(false);
            versioned.Version = expectedVersion + 1;
            _cache[entity.Id] = entity;
            _dirty = true;
            PersistIfRequired();
        }

        if (updated)
            _entityUpdated.OnNext(entity);
        else
            _entityAdded.OnNext(entity);

        return Task.FromResult(true);
    }

    public Task<bool> RemoveAsync(Guid id) => RemoveAsync(id, CancellationToken.None);

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TEntity? removed = default;
        using (_lock.Write())
        {
            EnsureCacheLoaded();
            if (_cache.TryGetValue(id, out removed))
            {
                _cache.Remove(id);
                _dirty = true;
                PersistIfRequired();
            }
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

        using (_lock.Write())
        {
            if (_dirty)
                PersistToFile();
        }

        _lock.Dispose();
        _entityAdded.Dispose();
        _entityUpdated.Dispose();
        _entityRemoved.Dispose();
    }
}








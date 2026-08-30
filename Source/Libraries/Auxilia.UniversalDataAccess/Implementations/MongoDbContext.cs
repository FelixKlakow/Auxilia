using Auxilia.UniversalDataAccess.Settings;
using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;

namespace Auxilia.UniversalDataAccess.Implementations;

/// <summary>
/// Generic EF Core <see cref="DbContext"/> that maps a single entity type
/// <typeparamref name="TEntity"/> to a MongoDB collection.
/// The collection name and optional entity-model customisation come from
/// the <see cref="MongoDbSettings{TEntity}"/> injected via DI.
/// </summary>
public sealed class MongoDbContext<TEntity> : DbContext
    where TEntity : class, IEntity
{
    private readonly MongoDbSettings<TEntity> _settings;

    // ReSharper disable once UnusedMember.Global – required by EF Core design tooling
    public MongoDbContext(
        DbContextOptions<MongoDbContext<TEntity>> options,
        MongoDbSettings<TEntity> settings)
        : base(options)
    {
        _settings = settings;
    }

    /// <summary>The typed set for all <typeparamref name="TEntity"/> documents.</summary>
    public DbSet<TEntity> Entities { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var entity = modelBuilder.Entity<TEntity>();
        entity.HasKey(e => e.Id);

        // Versioned entities get their Version enforced as an optimistic-concurrency token:
        // an update whose original Version no longer matches the stored document fails with
        // DbUpdateConcurrencyException (a filtered replace on MongoDB), which backs TrySaveAsync.
        if (typeof(IVersionedEntity).IsAssignableFrom(typeof(TEntity)))
            entity.Property(nameof(IVersionedEntity.Version)).IsConcurrencyToken();

        // ToCollection is a MongoDB EF Core extension — safe to skip for InMemory/SQL providers
        if (Database.ProviderName?.Contains("MongoDB", StringComparison.OrdinalIgnoreCase) == true)
            entity.ToCollection(_settings.CollectionName);

        // Let callers plug in OwnsOne, OwnsMany, indexes, etc.
        _settings.ConfigureEntity?.Invoke(entity);
    }
}



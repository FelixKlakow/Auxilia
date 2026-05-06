using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auxilia.UniversalDataAccess.Settings;

/// <summary>
/// Configuration settings for <see cref="Implementations.MongoDbEfDataAccess{TEntity}" />.
/// </summary>
/// <typeparam name="TEntity">The entity type this access layer manages.</typeparam>
public class MongoDbSettings<TEntity> where TEntity : class, IEntity
{
    /// <summary>MongoDB connection string. Defaults to <c>mongodb://localhost:27017</c>.</summary>
    public string ConnectionString { get; init; } = "mongodb://localhost:27017";

    /// <summary>The database name to use. Defaults to <c>Auxilia</c>.</summary>
    public string DatabaseName { get; init; } = "Auxilia";

    /// <summary>
    /// The MongoDB collection name. Defaults to the entity type's simple name so that
    /// <c>MongoDbSettings&lt;OrderEntity&gt;</c> uses the <c>OrderEntity</c> collection.
    /// </summary>
    public string CollectionName { get; init; } = typeof(TEntity).Name;

    /// <summary>
    /// Optional hook that lets callers extend the EF Core entity model, for example to
    /// configure owned types, indexes, or property mappings that cannot be inferred
    /// automatically (e.g. <c>entity.OwnsOne(...)</c>, <c>entity.OwnsMany(...)</c>).
    /// </summary>
    public Action<EntityTypeBuilder<TEntity>>? ConfigureEntity { get; init; }
}



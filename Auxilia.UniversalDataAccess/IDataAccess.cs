namespace Auxilia.UniversalDataAccess;

/// <summary>
/// CRUD Interface for various implementations of simple data access
/// </summary>
/// <typeparam name="TEntity">Data object to be persisted</typeparam>
public interface IDataAccess<TEntity> where TEntity : IEntity
{
    /// <summary>
    /// Emitted when a new Entity is added
    /// </summary>
    IObservable<TEntity> EntityAdded { get; }
    
    /// <summary>
    /// Emitted when an existing Entity is updated. The emitted entity already has the updated state
    /// </summary>
    IObservable<TEntity> EntityUpdated { get; }
    
    /// <summary>
    /// Emitted when an existing Entity is removed. The emitted entity is the state before the removal, which is now no longer persistent
    /// </summary>
    IObservable<TEntity> EntityRemoved { get; }

    /// <summary>
    /// Reads the Queryable for all Entities in this Data Access Layer
    /// </summary>
    /// <returns>Queryable which can be filtered before accessing for better runtime</returns>
    Task<IQueryable<TEntity>> ReadAsync() => ReadAsync(CancellationToken.None);
    
    /// <summary>
    /// Reads the Queryable for all Entities in this Data Access Layer
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the reading, will result in a <exception cref="TaskCanceledException">TaskCanceledException</exception> being thrown from this method</param>
    /// <returns>Queryable which can be filtered before accessing for better runtime</returns>
    Task<IQueryable<TEntity>> ReadAsync(CancellationToken cancellationToken);
    
    /// <summary>
    /// Finds an entity by ID or returns null if it is not saved in this Data Access Layer
    /// </summary>
    /// <param name="id">ID of the saved entity</param>
    /// <returns>Entity or null</returns>
    Task<TEntity?> ReadAsync(Guid id) => ReadAsync(id, CancellationToken.None);
    
    /// <summary>
    /// Finds an entity by ID or returns null if it is not saved in this Data Access Layer
    /// </summary>
    /// <param name="id">ID of the saved entity</param>
    /// <param name="cancellationToken">Token to cancel the reading, will result in a <exception cref="TaskCanceledException">TaskCanceledException</exception> being thrown from this method</param>
    /// <returns>Entity or null</returns>
    Task<TEntity?> ReadAsync(Guid id, CancellationToken cancellationToken);
    
    /// <summary>
    /// Saves the given entity. If an entity with the same ID already exists, it will be updated, otherwise a new entity will be created.
    /// </summary>
    /// <param name="entity">Entity to be persisted or updated</param>
    /// <returns><c>true</c> if an existing entity was updated, <c>false</c> if a new entity was added</returns>
    Task<bool> SaveAsync(TEntity entity) =>  SaveAsync(entity, CancellationToken.None);
    
    /// <summary>
    /// Saves the given entity. If an entity with the same ID already exists, it will be updated, otherwise a new entity will be created.
    /// </summary>
    /// <param name="entity">Entity to be persisted or updated</param>
    /// <param name="cancellationToken">Token to cancel the writing, will result in a <exception cref="TaskCanceledException">TaskCanceledException</exception> being thrown from this method.</param>
    /// <returns><c>true</c> if an existing entity was updated, <c>false</c> if a new entity was added</returns>
    Task<bool> SaveAsync(TEntity entity, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the entity with the given ID. If no such entity exists, nothing happens and <c>false</c> is returned. If an entity was removed, <c>true</c> is returned.
    /// </summary>
    /// <param name="id">Identifier of the entity to be removed</param>
    /// <returns><c>true</c> if an actual deletion was triggered</returns>
    Task<bool> RemoveAsync(Guid id);
    
    /// <summary>
    /// Removes the entity with the given ID. If no such entity exists, nothing happens and <c>false</c> is returned. If an entity was removed, <c>true</c> is returned.
    /// </summary>
    /// <param name="id">Identifier of the entity to be removed</param>
    /// <param name="cancellationToken">Token to cancel the deletion, will result in a <exception cref="TaskCanceledException">TaskCanceledException</exception> being thrown from this method.</param>
    /// <returns><c>true</c> if an actual deletion was triggered</returns>
    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken);
}
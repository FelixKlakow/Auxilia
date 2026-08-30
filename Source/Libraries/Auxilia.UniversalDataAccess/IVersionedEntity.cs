namespace Auxilia.UniversalDataAccess;

/// <summary>
/// An entity carrying a store-managed optimistic-concurrency version. Every successful save
/// (plain or conditional) stamps <see cref="Version"/> to the stored version + 1 (1 on first
/// insert) — callers never assign it. Required by
/// <see cref="IDataAccess{TEntity}.TrySaveAsync(TEntity,long,System.Threading.CancellationToken)"/>.
/// </summary>
public interface IVersionedEntity : IEntity
{
    /// <summary>Monotonically increasing, store-managed; 0 means "never saved".</summary>
    long Version { get; set; }
}

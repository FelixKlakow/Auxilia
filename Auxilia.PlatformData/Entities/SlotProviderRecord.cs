using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>Registered slot-handler plugin; one record per provider type.</summary>
public sealed record SlotProviderRecord : IEntity
{
    public Guid Id { get; init; }
    public required string ProviderType { get; init; }
    public required string DllPath { get; init; }

    public static Guid IdFor(string providerType) => DeterministicGuid.For("slot-provider", providerType);
}

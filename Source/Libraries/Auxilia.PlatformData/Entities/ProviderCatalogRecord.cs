using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Admin curation of one registered slot provider (#19): whether users may pick it when
/// configuring workflows, the slot-kind category it is offered under, and presentation
/// overrides for its manifest-declared setting descriptors. Kept separate from
/// <see cref="SlotProviderRecord"/> so re-registration never wipes curation.
/// </summary>
public sealed record ProviderCatalogRecord : IEntity
{
    public Guid Id { get; init; }
    public required string ProviderType { get; init; }
    /// <summary>Deny-by-default: a provider is offered to users only after an admin enables it.</summary>
    public bool Available { get; init; }
    /// <summary>Free-form slot-kind tag (e.g. "task-source", "repository"); empty = uncategorized.</summary>
    public string Category { get; init; } = "";
    /// <summary>Serialized admin overrides (label/help text/default per setting key).</summary>
    public string DescriptorOverridesJson { get; init; } = "[]";

    /// <summary>
    /// Serialized <c>AccessGrant</c> list restricting who may BIND this catalog entry into a
    /// run (slot providers and environment layers alike); empty = everyone. Distinct from
    /// <see cref="Available"/>, the global on/off switch.
    /// </summary>
    public string GrantsJson { get; init; } = "[]";

    /// <summary>Case-insensitive: every case variant of a provider type converges on one record.</summary>
    public static Guid IdFor(string providerType)
        => DeterministicGuid.For("provider-catalog", providerType.ToLowerInvariant());
}

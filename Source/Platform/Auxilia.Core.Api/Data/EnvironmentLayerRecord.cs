using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// One admin-managed session environment: a capability name plus one initialization-script
/// variant per base environment it supports (+ optional pinned software version). The runner
/// builds the container layer on the fly from the fragment the Core generates out of the
/// variant matching its base. The matching provider-catalog entry (category "environment") is
/// maintained alongside by <c>EnvironmentLayerService</c>.
/// </summary>
public sealed record EnvironmentLayerRecord : IEntity
{
    public Guid Id { get; init; }

    public required string ProviderType { get; init; }

    public string? Description { get; init; }

    /// <summary>Serialized <c>EnvironmentLayerVariant</c> list — one setup script per supported base.</summary>
    public required string VariantsJson { get; init; }

    /// <summary>Optional pinned software version — the seam for later pre-built versioned containers.</summary>
    public string? Version { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public Guid? UpdatedBy { get; init; }

    public static Guid IdFor(string providerType) => DeterministicGuid.For("environment-layer", providerType);
}

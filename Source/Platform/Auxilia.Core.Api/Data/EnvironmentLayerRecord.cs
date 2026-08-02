using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// One admin-managed session environment: base environment + initialization script (+ optional
/// pinned software version). The runner builds the container layer on the fly from the fragment
/// the Core generates out of the script. The matching provider-catalog entry (category
/// "environment") is maintained alongside by <c>EnvironmentLayerService</c>.
/// </summary>
public sealed record EnvironmentLayerRecord : IEntity
{
    public Guid Id { get; init; }

    public required string ProviderType { get; init; }

    public string? Description { get; init; }

    /// <summary>The base this environment initializes on (e.g. "linux", "windows").</summary>
    public required string BaseEnvironment { get; init; }

    /// <summary>The initialization script executed while building the environment layer.</summary>
    public required string SetupScript { get; init; }

    /// <summary>Optional pinned software version — the seam for later pre-built versioned containers.</summary>
    public string? Version { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public Guid? UpdatedBy { get; init; }

    public static Guid IdFor(string providerType) => DeterministicGuid.For("environment-layer", providerType);
}

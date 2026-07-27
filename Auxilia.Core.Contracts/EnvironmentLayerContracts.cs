namespace Auxilia.Core.Contracts;

/// <summary>
/// One admin-managed session environment: a base environment (e.g. "linux", "windows") plus an
/// initialization script that makes the environment ready — the runner builds the container
/// layer on the fly from it (and caches by content). <see cref="Version"/> optionally pins a
/// software version (and is the seam for later pre-built, versioned environment containers).
/// Managing environments requires the provider-catalog permission.
/// </summary>
public sealed record EnvironmentLayerDto(
    string ProviderType,
    string? Description,
    string BaseEnvironment,
    string SetupScript,
    string? Version,
    DateTimeOffset UpdatedUtc);

/// <summary>Creates or updates an environment (and its provider-catalog entry).</summary>
public sealed record UpsertEnvironmentLayer(
    string ProviderType,
    string? Description,
    string SetupScript,
    string BaseEnvironment = EnvironmentBases.Linux,
    string? Version = null);

/// <summary>
/// Well-known base environments. An open vocabulary — runners advertise what they can host;
/// today only Linux runners exist.
/// </summary>
public static class EnvironmentBases
{
    public const string Linux = "linux";
    public const string Windows = "windows";
}

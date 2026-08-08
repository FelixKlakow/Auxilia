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
    DateTimeOffset UpdatedUtc,
    string? BaseVersion = null)
{
    /// <summary>Who may bind this layer into a run; empty = everyone.</summary>
    public IReadOnlyList<AccessGrant> Grants { get; init; } = [];
}

/// <summary>
/// Restricts who may bind an environment layer into a run: empty grants keep the layer open;
/// non-empty grants admit only the listed subjects — enforced at dispatch, like connector access.
/// </summary>
public sealed record SetEnvironmentLayerGrants(IReadOnlyList<AccessGrant> Grants);

/// <summary>
/// Creates or updates an environment (and its provider-catalog entry).
/// <see cref="BaseVersion"/> optionally pins a registered base version (see
/// <see cref="EnvironmentBaseDto"/>); a pinned version must exist in the base catalog, an
/// unpinned layer composes with any version of its base.
/// </summary>
public sealed record UpsertEnvironmentLayer(
    string ProviderType,
    string? Description,
    string SetupScript,
    string BaseEnvironment = EnvironmentBases.Linux,
    string? Version = null,
    string? BaseVersion = null);

/// <summary>
/// One admin-managed base version of an environment base: a (name, version) pair like
/// ("linux", "ubuntu-24.04") or ("windows", "server-2022"). Bases are an open, configurable
/// vocabulary — the catalog is what editors offer and what layer base-version pins validate
/// against. Managed over the provider-catalog permission, like layers.
/// </summary>
public sealed record EnvironmentBaseDto(
    string Name,
    string Version,
    string? Description,
    DateTimeOffset UpdatedUtc);

/// <summary>Creates or updates one base version of the environment-base catalog.</summary>
public sealed record UpsertEnvironmentBase(
    string Name,
    string Version,
    string? Description = null);

/// <summary>
/// Well-known base environment NAMES. An open vocabulary — runners advertise what they can
/// host (today only Linux runners exist); versions of a name live in the admin-managed base
/// catalog (<see cref="EnvironmentBaseDto"/>).
/// </summary>
public static class EnvironmentBases
{
    public const string Linux = "linux";
    public const string Windows = "windows";
}

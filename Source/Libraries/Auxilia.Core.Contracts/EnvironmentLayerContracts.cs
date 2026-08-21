namespace Auxilia.Core.Contracts;

/// <summary>
/// One base-specific variant of an environment layer: the initialization script that makes the
/// environment ready on that base (e.g. apt-get on linux, PowerShell on windows) — the runner
/// builds the container layer on the fly from it (and caches by content). <see cref="BaseVersion"/>
/// optionally pins a registered base version; null composes with any version of the base.
/// </summary>
public sealed record EnvironmentLayerVariant(
    string BaseEnvironment,
    string SetupScript,
    string? BaseVersion = null);

/// <summary>
/// One admin-managed session environment: a capability name (what workflows ask for, e.g.
/// "dotnet-10") carrying one setup-script variant per base environment it supports — a layer
/// with both a linux and a windows variant binds into runs on either base.
/// <see cref="Version"/> optionally pins a software version (and is the seam for later
/// pre-built, versioned environment containers). Managing environments requires the
/// provider-catalog permission.
/// </summary>
public sealed record EnvironmentLayerDto(
    string ProviderType,
    string? Description,
    IReadOnlyList<EnvironmentLayerVariant> Variants,
    string? Version,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>
    /// Who may bind this layer into a run; empty follows the platform's
    /// <c>security.default-resource-access</c> setting (restricted = administrators only).
    /// </summary>
    public IReadOnlyList<AccessGrant> Grants { get; init; } = [];
}

/// <summary>
/// Restricts who may bind an environment layer into a run: empty grants defer to the platform
/// default-access setting; non-empty grants admit only the listed subjects — enforced at
/// dispatch, like connector access. Administrators always pass.
/// </summary>
public sealed record SetEnvironmentLayerGrants(IReadOnlyList<AccessGrant> Grants);

/// <summary>
/// Creates or updates ONE variant of an environment layer, keyed (ProviderType, BaseEnvironment):
/// the layer is created on its first variant, and adding a variant for a new base is the same
/// call. <see cref="Description"/> and <see cref="Version"/> are layer-wide and updated on every
/// upsert. <see cref="BaseVersion"/> optionally pins a registered base version (see
/// <see cref="EnvironmentBaseDto"/>); a pinned version must exist in the base catalog, an
/// unpinned variant composes with any version of its base.
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
    DateTimeOffset UpdatedUtc,
    string? ImageReference = null);

/// <summary>
/// Creates or updates one base version of the environment-base catalog.
/// <paramref name="ImageReference"/> optionally pins the base to a concrete container image
/// (digest-pinned, <c>…@sha256:…</c>) — bases carrying one become the runtime-spawnable
/// vocabulary of pod-controlled workflows (run-pod design §"pod controller"): a workflow may
/// spawn companions FROM registered base images only, never from arbitrary references.
/// </summary>
public sealed record UpsertEnvironmentBase(
    string Name,
    string Version,
    string? Description = null,
    string? ImageReference = null);

/// <summary>
/// One base a catalog entry composes on: the base name plus the entry's optional version pin for
/// that base. An environment layer's catalog entry carries one ref per variant.
/// </summary>
public sealed record EnvironmentBaseRef(string Name, string? Version = null);

/// <summary>
/// Well-known base environment NAMES. An open vocabulary — runners advertise what they can
/// host; versions of a name live in the admin-managed base
/// catalog (<see cref="EnvironmentBaseDto"/>).
/// </summary>
public static class EnvironmentBases
{
    public const string Linux = "linux";
    public const string Windows = "windows";
}

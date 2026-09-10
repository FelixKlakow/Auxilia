using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// One base version of the environment-base catalog: a (name, version) pair like
/// ("linux", "ubuntu-24.04"). An open, admin-configurable vocabulary — what editors offer as
/// bases and what layer base-version pins validate against. Maintained by
/// <c>EnvironmentBaseService</c>.
/// </summary>
public sealed record EnvironmentBaseRecord : IEntity
{
    public Guid Id { get; init; }

    /// <summary>The base name ("linux", "windows", ...); lowercased on write.</summary>
    public required string Name { get; init; }

    /// <summary>The version within the name ("ubuntu-24.04", "server-2022", ...).</summary>
    public required string Version { get; init; }

    public string? Description { get; init; }

    /// <summary>Optional digest-pinned image this base resolves to — the runtime-spawnable seam.</summary>
    public string? ImageReference { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public Guid? UpdatedBy { get; init; }

    public static Guid IdFor(string name, string version)
        => DeterministicGuid.For("environment-base", name, version);
}

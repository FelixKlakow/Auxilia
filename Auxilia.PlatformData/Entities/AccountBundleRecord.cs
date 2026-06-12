using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// A set of credentials grouped by purpose (ARCHITECTURE §11), owned by a principal and
/// verified by pre-flight. <see cref="ProtectedSecretsJson"/> is the JSON credential
/// dictionary run through <see cref="Protection.ISettingsProtector"/> — secret values are
/// write-only and never leave the store in the clear.
/// </summary>
public sealed record AccountBundleRecord : IEntity
{
    public Guid Id { get; init; }
    public Guid OwnerPrincipalId { get; init; }
    public required string Name { get; init; }
    /// <summary>Purpose scope, e.g. "SourceControl", "TaskSource", "Ai", "Resource".</summary>
    public required string BundleType { get; init; }
    public required string ProtectedSecretsJson { get; init; }

    public static Guid IdFor(string name) => DeterministicGuid.For("account-bundle", name);
}

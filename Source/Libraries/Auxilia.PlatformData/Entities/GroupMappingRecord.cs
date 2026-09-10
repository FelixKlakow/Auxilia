using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>Maps an identity-provider group claim to a built-in role at sign-in.</summary>
public sealed record GroupMappingRecord : IEntity
{
    public Guid Id { get; init; }
    public required string IdentityProvider { get; init; }
    public required string GroupClaim { get; init; }
    public required string RoleName { get; init; }

    public static Guid IdFor(string identityProvider, string groupClaim, string roleName)
        => DeterministicGuid.For("group-mapping", identityProvider, groupClaim, roleName);
}

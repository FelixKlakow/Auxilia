using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>Assignment of a built-in role to a principal.</summary>
public sealed record RoleAssignmentRecord : IEntity
{
    public Guid Id { get; init; }
    public Guid PrincipalId { get; init; }
    public required string RoleName { get; init; }
    /// <summary>"Direct" (administered) or "GroupMapping" (derived from IdP groups at sign-in).</summary>
    public required string Source { get; init; }

    public static Guid IdFor(Guid principalId, string roleName)
        => DeterministicGuid.For("role-assignment", principalId.ToString("D"), roleName);
}

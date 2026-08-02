using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>A role granted to every member of a group.</summary>
public sealed record GroupRoleRecord : IEntity
{
    public Guid Id { get; init; }
    public Guid GroupId { get; init; }
    public required string RoleName { get; init; }

    public static Guid IdFor(Guid groupId, string roleName)
        => DeterministicGuid.For("group-role", groupId.ToString("D"), roleName);
}

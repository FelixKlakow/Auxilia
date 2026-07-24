using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>Membership of a principal in a group.</summary>
public sealed record GroupMembershipRecord : IEntity
{
    public Guid Id { get; init; }
    public Guid GroupId { get; init; }
    public Guid PrincipalId { get; init; }

    public static Guid IdFor(Guid groupId, Guid principalId)
        => DeterministicGuid.For("group-membership", groupId.ToString("D"), principalId.ToString("D"));
}

namespace Auxilia.Core.Api;

/// <summary>Request to map an identity-provider group claim to a built-in role at sign-in.</summary>
public sealed record CreateGroupMappingRequest(string IdentityProvider, string GroupClaim, string RoleName);

/// <summary>An administered directory group → role mapping.</summary>
public sealed record GroupMappingDto(Guid Id, string IdentityProvider, string GroupClaim, string RoleName);

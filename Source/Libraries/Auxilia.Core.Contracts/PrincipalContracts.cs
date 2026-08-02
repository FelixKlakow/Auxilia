namespace Auxilia.Core.Contracts;

/// <summary>
/// A principal (human or service) with its resolved role assignments and free-form tags. Never
/// carries credential material; a service principal's API key is returned only once, at creation,
/// via <see cref="CreatedApiKeyPrincipal"/>.
/// </summary>
public sealed record PrincipalDto(
    Guid Id,
    string Kind,
    string DisplayName,
    string Status,
    string? ExternalSubject,
    IReadOnlyList<PrincipalRoleDto> Roles,
    IReadOnlyList<string> Tags);

/// <summary>One role a principal holds, tagged with where it comes from.</summary>
/// <param name="Source">"Direct" (administered), "Group" (via group membership), or "GroupMapping" (from IdP groups at sign-in).</param>
public sealed record PrincipalRoleDto(string RoleName, string Source);

/// <summary>Filter for querying principals. Unset fields are ignored; paging is always applied.</summary>
public sealed record PrincipalQuery(
    string? Kind = null,
    bool? Enabled = null,
    string? Search = null,
    int Skip = 0,
    int Take = 50,
    string? Tag = null);

/// <summary>Create a local human principal that signs in with a username and password.</summary>
public sealed record CreateHumanPrincipalRequest(string DisplayName, string Username, string Password);

/// <summary>Create a service principal (kind "Service" — classify further with tags) that
/// authenticates with a generated API key.</summary>
public sealed record CreateApiKeyPrincipalRequest(string DisplayName);

/// <summary>
/// The created service principal together with its API key. The key is shown once here and is
/// write-only thereafter — the Core stores only its hash and never returns it again.
/// </summary>
public sealed record CreatedApiKeyPrincipal(PrincipalDto Principal, string ApiKey);

/// <summary>Replaces a principal's free-form tags (the classification axis — e.g. "ai-agent",
/// a team, an environment); empty clears them.</summary>
public sealed record SetPrincipalTagsRequest(IReadOnlyList<string> Tags);

/// <summary>Assign a Direct built-in role to a principal.</summary>
public sealed record AssignRoleRequest(string RoleName);

/// <summary>Enable or disable a principal.</summary>
public sealed record SetPrincipalEnabledRequest(bool Enabled);

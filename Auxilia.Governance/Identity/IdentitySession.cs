namespace Auxilia.Governance.Identity;

/// <summary>An authenticated session: the principal and its resolved role names.</summary>
public sealed record IdentitySession(
    Guid PrincipalId,
    string Kind,
    string DisplayName,
    IReadOnlyList<string> Roles);

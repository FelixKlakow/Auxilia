namespace Auxilia.Governance.Identity;

/// <summary>
/// A validated identity from an external provider (e.g. Entra ID). The provider verifies these
/// claims upstream (signature, issuer, audience, expiry); the Core trusts them and provisions a
/// principal from the subject. <see cref="Groups"/> carries the directory group claims that later
/// drive role mapping.
/// </summary>
public sealed record ExternalIdentity(
    string Provider,
    string Subject,
    string DisplayName,
    string? Email,
    IReadOnlyList<string> Groups);

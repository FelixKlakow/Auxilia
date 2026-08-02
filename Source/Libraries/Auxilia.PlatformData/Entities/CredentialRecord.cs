using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// A local credential owned by the Local identity provider: a password (PBKDF2 hash, looked up
/// by username) or an API key (SHA-256 hash, looked up directly by key hash). External-IdP
/// principals never have credential records.
/// </summary>
public sealed record CredentialRecord : IEntity
{
    public Guid Id { get; init; }
    public Guid PrincipalId { get; init; }
    /// <summary>"Password" or "ApiKey".</summary>
    public required string Kind { get; init; }
    /// <summary>Username for passwords; an opaque key identifier for API keys.</summary>
    public required string Identifier { get; init; }
    /// <summary>PBKDF2 envelope for passwords; hex SHA-256 of the raw key for API keys.</summary>
    public required string SecretHash { get; init; }

    public static Guid IdForPassword(string username) => DeterministicGuid.For("credential-password", username);
    public static Guid IdForApiKeyHash(string keyHashHex) => DeterministicGuid.For("credential-apikey", keyHashHex);
}

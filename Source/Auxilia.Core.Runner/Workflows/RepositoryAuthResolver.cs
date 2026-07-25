using System.Text.Json;
using Auxilia.Workflows.Crypto;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>A repository's resolved clone credential: the token and an optional username.</summary>
public sealed record RepositoryAuth(string? Username, string Token);

public interface IRepositoryAuthResolver
{
    /// <summary>Resolves a repository's clone credential, or null when it cannot be obtained.</summary>
    Task<RepositoryAuth?> ResolveAsync(
        Guid coreRunId, string resolutionToken, string authSlotName, CancellationToken ct);
}

/// <summary>
/// Resolves a repository's clone credential just-in-time at dispatch: presents a fresh public key to
/// the Core's slot-resolution endpoint (via <see cref="ICoreCredentialClient"/>), decrypts the
/// returned connector settings locally, and returns the token (and optional username). The credential
/// lives only in this process's memory for the clone — never on the bus, never in the workflow
/// container (the Workspace Manager strips it from the mounted copy).
/// </summary>
public sealed class RepositoryAuthResolver(ICoreCredentialClient credentialClient) : IRepositoryAuthResolver
{
    private static readonly string[] TokenKeys = ["token", "pat", "password", "accessToken"];
    private static readonly string[] UsernameKeys = ["username", "user"];

    public async Task<RepositoryAuth?> ResolveAsync(
        Guid coreRunId, string resolutionToken, string authSlotName, CancellationToken ct)
    {
        using var keyPair = new EphemeralKeyPair();
        var resolved = await credentialClient.ResolveAsync(
            coreRunId, resolutionToken, authSlotName, keyPair.PublicKeyBase64, ct);
        if (resolved is null)
            return null;

        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                           keyPair.Decrypt(resolved.EncryptedSettings))
                       ?? new Dictionary<string, string>();
        var token = Find(settings, TokenKeys);
        return token is null ? null : new RepositoryAuth(Find(settings, UsernameKeys), token);
    }

    private static string? Find(Dictionary<string, string> settings, string[] keys)
    {
        foreach (var key in keys)
            foreach (var (settingKey, value) in settings)
                if (string.Equals(settingKey, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
                    return value;
        return null;
    }
}

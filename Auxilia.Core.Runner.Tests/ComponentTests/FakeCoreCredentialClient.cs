using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Core.Runner.Workflows;

namespace Auxilia.Core.Runner.Tests.ComponentTests;

/// <summary>
/// Component-test stub for <see cref="ICoreCredentialClient"/>. By default it resolves nothing
/// (a failed activation); a test that needs a successful JIT activation sets <see cref="OnResolve"/>
/// (or uses <see cref="RespondWith"/>) so the fake encrypts real settings for the requesting
/// instance's public key, exactly as the Core would.
/// </summary>
public sealed class FakeCoreCredentialClient : ICoreCredentialClient
{
    /// <summary>Given the slot name and the instance's public key, produces the resolved credential (or null).</summary>
    public Func<string, string, ResolvedSlotCredential?>? OnResolve { get; set; }

    /// <summary>The last (runId, token, slot) the runner asked to resolve — for assertions.</summary>
    public (Guid RunId, string Token, string Slot)? LastCall { get; private set; }

    public Task<ResolvedSlotCredential?> ResolveAsync(
        Guid coreRunId, string resolutionToken, string slotName, string publicKey, CancellationToken ct)
    {
        LastCall = (coreRunId, resolutionToken, slotName);
        return Task.FromResult(OnResolve?.Invoke(slotName, publicKey));
    }

    /// <summary>
    /// Configures the fake to resolve every slot to <paramref name="providerType"/> with
    /// <paramref name="settings"/> RSA-encrypted (OAEP-SHA256) for the instance's public key —
    /// the same scheme the workflow SDK decrypts.
    /// </summary>
    public void RespondWith(
        string providerType, IReadOnlyDictionary<string, string> settings, DateTimeOffset expiresUtc)
        => OnResolve = (_, publicKey) =>
            new ResolvedSlotCredential(providerType, EncryptFor(publicKey, settings), expiresUtc);

    private static string EncryptFor(string publicKeyBase64, IReadOnlyDictionary<string, string> settings)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        var cipher = rsa.Encrypt(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings)), RSAEncryptionPadding.OaepSHA256);
        return Convert.ToBase64String(cipher);
    }
}

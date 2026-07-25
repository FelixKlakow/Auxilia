using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Resolves a run's slot credentials from Core connectors and RSA-encrypts them for the
/// requesting instance — the Core-side of just-in-time credential delivery. The encryption
/// happens here so plaintext connector settings never enter the runner process; the runner only
/// relays the ciphertext. Every resolution is authorized by the run-scoped token and audited.
/// </summary>
public sealed class SlotCredentialResolver(
    IDataAccess<CoreRunResolutionRecord> store,
    ConnectorService connectors,
    AuditLog audit,
    TimeProvider clock,
    IOptions<CoreApiSettings> settings)
{
    /// <summary>Records the run's resolution context at dispatch time (references only, never secrets).</summary>
    public Task StashAsync(Guid runId, string resolutionToken, IReadOnlyList<SlotBinding> bindings, CancellationToken ct)
        => store.SaveAsync(new CoreRunResolutionRecord
        {
            Id = runId,
            ResolutionToken = resolutionToken,
            SlotBindingsJson = JsonSerializer.Serialize(bindings),
            CreatedUtc = clock.GetUtcNow()
        }, ct);

    /// <summary>
    /// Validates the run-scoped token, resolves the slot's connector settings, and encrypts them
    /// for <paramref name="publicKeyBase64"/>. Returns a 403-style failure for a token mismatch
    /// and a 404-style failure for an unknown run/slot.
    /// </summary>
    public async Task<(bool Success, string? Error, ResolvedSlotCredential? Credential)> ResolveAsync(
        Guid runId, string resolutionToken, string slotName, string publicKeyBase64, CancellationToken ct)
    {
        var record = await store.ReadAsync(runId, ct);
        if (record is null)
            return (false, "unknown run", null);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(record.ResolutionToken), Encoding.UTF8.GetBytes(resolutionToken)))
        {
            await audit.AppendAsync(
                "core-api", "workflow.slot-credential.rejected", runId.ToString(), "invalid-resolution-token", ct: ct);
            return (false, "invalid resolution token", null);
        }

        var bindings = JsonSerializer.Deserialize<List<SlotBinding>>(record.SlotBindingsJson) ?? [];
        var binding = bindings.FirstOrDefault(b => b.SlotName == slotName);
        if (binding is null)
            return (false, $"no binding for slot '{slotName}'", null);

        string providerType;
        IReadOnlyDictionary<string, string> resolvedSettings;
        if (binding.ConnectorId is { } connectorId)
        {
            if (await connectors.ResolveSettingsAsync(connectorId, ct) is not { } settingsMap)
                return (false, $"connector '{connectorId}' not found", null);
            resolvedSettings = settingsMap;
            providerType = binding.ProviderType
                           ?? (await connectors.GetAsync(connectorId, ct))?.ProviderType
                           ?? string.Empty;
        }
        else
        {
            providerType = binding.ProviderType ?? string.Empty;
            resolvedSettings = binding.Settings ?? new Dictionary<string, string>();
        }

        if (string.IsNullOrEmpty(providerType))
            return (false, $"slot '{slotName}' has no provider type", null);

        var (ok, error, encrypted) = EncryptForPublicKey(resolvedSettings, publicKeyBase64);
        if (!ok)
            return (false, error, null);

        await audit.AppendAsync(
            "core-api", "workflow.slot-credential.resolved", runId.ToString(), slotName, ct: ct);
        var expiresUtc = clock.GetUtcNow() + settings.Value.SlotCredentialLifetime;
        return (true, null, new ResolvedSlotCredential(providerType, encrypted!, expiresUtc));
    }

    private static (bool Success, string? Error, string? Encrypted) EncryptForPublicKey(
        IReadOnlyDictionary<string, string> settings, string publicKeyBase64)
    {
        byte[] der;
        try
        {
            der = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException)
        {
            return (false, "public key is not valid base-64", null);
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(der, out _);
        }
        catch (CryptographicException)
        {
            return (false, "failed to import RSA public key", null);
        }

        var json = JsonSerializer.Serialize(settings);
        var cipher = rsa.Encrypt(Encoding.UTF8.GetBytes(json), RSAEncryptionPadding.OaepSHA256);
        return (true, null, Convert.ToBase64String(cipher));
    }
}

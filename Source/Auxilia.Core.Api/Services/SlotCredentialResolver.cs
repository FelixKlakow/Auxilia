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
    DelegatedTokenStore delegatedTokens,
    IDelegatedTokenExchange delegatedExchange,
    AuditLog audit,
    TimeProvider clock,
    IOptions<CoreApiSettings> settings)
{
    /// <summary>Records the run's resolution context at dispatch time (references only, never secrets).</summary>
    public Task StashAsync(
        Guid runId, string resolutionToken, IReadOnlyList<SlotBinding> bindings,
        Guid? triggeredBy, CancellationToken ct)
        => store.SaveAsync(new CoreRunResolutionRecord
        {
            Id = runId,
            ResolutionToken = resolutionToken,
            SlotBindingsJson = JsonSerializer.Serialize(bindings),
            TriggeredByPrincipalId = triggeredBy,
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
            return await RejectAsync(runId, "unknown-run", "unknown run", ct);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(record.ResolutionToken), Encoding.UTF8.GetBytes(resolutionToken)))
            return await RejectAsync(runId, "invalid-resolution-token", "invalid resolution token", ct);

        var bindings = JsonSerializer.Deserialize<List<SlotBinding>>(record.SlotBindingsJson) ?? [];
        var binding = bindings.FirstOrDefault(b => b.SlotName == slotName);
        if (binding is null)
            return await RejectAsync(runId, "no-binding", $"no binding for slot '{slotName}'", ct);

        string providerType;
        IReadOnlyDictionary<string, string> resolvedSettings;
        if (binding.DelegatedResource is { } resource)
        {
            // On-behalf-of (OBO): mint a downstream token as the triggering user, just-in-time. The
            // user's retained token is the only stored secret; the exchanged token is never persisted.
            if (record.TriggeredByPrincipalId is not { } principalId)
                return await RejectAsync(runId, "delegation-no-principal",
                    "a delegated slot requires a triggering principal", ct);
            if (await delegatedTokens.GetAsync(principalId, ct) is not { } userToken)
                return await RejectAsync(runId, "delegation-no-token",
                    "delegated access requires a recent interactive sign-in", ct);
            if (await delegatedExchange.ExchangeAsync(userToken, resource, ct) is not { } downstreamToken)
                return await RejectAsync(runId, "delegation-exchange-failed",
                    "on-behalf-of token exchange failed", ct);
            providerType = binding.ProviderType ?? string.Empty;
            resolvedSettings = new Dictionary<string, string> { ["accessToken"] = downstreamToken };
        }
        else if (binding.ConnectorId is { } connectorId)
        {
            if (await connectors.ResolveSettingsAsync(connectorId, ct) is not { } settingsMap)
                return await RejectAsync(runId, "connector-not-found", $"connector '{connectorId}' not found", ct);
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
            return await RejectAsync(runId, "no-provider-type", $"slot '{slotName}' has no provider type", ct);

        var (ok, error, encrypted) = EncryptForPublicKey(resolvedSettings, publicKeyBase64);
        if (!ok)
            return await RejectAsync(runId, "encrypt-failed", error!, ct);

        await audit.AppendAsync(
            "core-api", "workflow.slot-credential.resolved", runId.ToString(), slotName, ct: ct);
        var expiresUtc = clock.GetUtcNow() + settings.Value.SlotCredentialLifetime;
        return (true, null, new ResolvedSlotCredential(providerType, encrypted!, expiresUtc));
    }

    private async Task<(bool Success, string? Error, ResolvedSlotCredential? Credential)> RejectAsync(
        Guid runId, string reason, string error, CancellationToken ct)
    {
        await audit.AppendAsync("core-api", "workflow.slot-credential.rejected", runId.ToString(), reason, ct: ct);
        return (false, error, null);
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

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
/// relays the ciphertext. Every resolution is authorized by the run-scoped token — which stops
/// authorizing the moment the run reaches a terminal state, although the stash itself is kept
/// for rerun — and audited.
/// </summary>
public sealed class SlotCredentialResolver(
    IDataAccess<CoreRunResolutionRecord> store,
    IDataAccess<CoreRunRecord> runs,
    ConnectorService connectors,
    ConnectorTokenRefresher tokenRefresher,
    DelegatedTokenStore delegatedTokens,
    IDelegatedTokenExchange delegatedExchange,
    SlotBindingSecrets bindingSecrets,
    AuditLog audit,
    TimeProvider clock,
    IOptions<CoreApiSettings> settings)
{
    public const string InvalidTokenError = "invalid resolution token";
    public const string RunEndedError = "the run has ended — its resolution token no longer authorizes";

    /// <summary>
    /// Records the run's resolution context at dispatch time (references only, never plaintext
    /// secrets — inline Secret-kind settings arrive already protected, and the token itself is
    /// stored only as its digest). The workspace and configuration references let a rerun
    /// re-apply the access gates of the original dispatch.
    /// </summary>
    public Task StashAsync(
        Guid runId, string resolutionToken, IReadOnlyList<SlotBinding> bindings,
        Guid? triggeredBy, string? dispatchCommandJson = null,
        IReadOnlyList<Guid>? workspaceIds = null, Guid? configurationId = null,
        CancellationToken ct = default)
        => store.SaveAsync(new CoreRunResolutionRecord
        {
            Id = runId,
            ResolutionTokenHash = ResolutionTokens.Hash(resolutionToken),
            SlotBindingsJson = JsonSerializer.Serialize(bindings),
            TriggeredByPrincipalId = triggeredBy,
            DispatchCommandJson = dispatchCommandJson,
            WorkspaceIdsJson = JsonSerializer.Serialize(workspaceIds ?? []),
            ConfigurationId = configurationId,
            CreatedUtc = clock.GetUtcNow()
        }, ct);

    /// <summary>
    /// Whether <paramref name="resolutionToken"/> currently authorizes run-scoped reads for
    /// <paramref name="runId"/>: the digest must match the stash AND the run must not have
    /// ended — the token is a capability of a live run, not of its retained stash. Every
    /// refusal is audited; shared by slot resolution and the runner's package / layer downloads.
    /// </summary>
    public async Task<(bool Ok, string? Error)> AuthorizeAsync(
        Guid runId, string resolutionToken, CancellationToken ct)
    {
        var (record, error) = await AuthorizedStashAsync(runId, resolutionToken, ct);
        return (record is not null, error);
    }

    private async Task<(CoreRunResolutionRecord? Record, string? Error)> AuthorizedStashAsync(
        Guid runId, string resolutionToken, CancellationToken ct)
    {
        var record = await store.ReadAsync(runId, ct);
        if (record is null)
            return await RefuseAsync(runId, "unknown-run", "unknown run", ct);
        if (!ResolutionTokens.Matches(record.ResolutionTokenHash, resolutionToken))
            return await RefuseAsync(runId, "invalid-resolution-token", InvalidTokenError, ct);
        // The run row lives under the command id until the runner's claim rekeys it to the
        // instance id (CommandId alias) — check whichever exists.
        var run = await runs.ReadAsync(runId, ct)
                  ?? (await runs.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == runId);
        if (run is not null && CoreRunStates.IsTerminal(run.State))
            return await RefuseAsync(runId, "run-ended", RunEndedError, ct);
        return (record, null);
    }

    private async Task<(CoreRunResolutionRecord? Record, string? Error)> RefuseAsync(
        Guid runId, string reason, string error, CancellationToken ct)
    {
        await audit.AppendAsync("core-api", "workflow.slot-credential.rejected", runId.ToString(), reason, ct: ct);
        return (null, error);
    }


    /// <summary>The stash of a past dispatch (bindings, principal, gated references) — for rerun.</summary>
    internal async Task<RunResolutionStash?> GetStashAsync(Guid runId, CancellationToken ct)
        => await store.ReadAsync(runId, ct) is { } record
            ? new RunResolutionStash(
                JsonSerializer.Deserialize<List<SlotBinding>>(record.SlotBindingsJson) ?? [],
                record.TriggeredByPrincipalId,
                JsonSerializer.Deserialize<List<Guid>>(record.WorkspaceIdsJson) ?? [],
                record.ConfigurationId)
            : null;

    /// <summary>
    /// Validates the run-scoped token, resolves the slot's connector settings, and encrypts them
    /// for <paramref name="publicKeyBase64"/>. Returns a 403-style failure for a token mismatch
    /// and a 404-style failure for an unknown run/slot.
    /// </summary>
    public async Task<(bool Success, string? Error, ResolvedSlotCredential? Credential)> ResolveAsync(
        Guid runId, string resolutionToken, string slotName, string publicKeyBase64, CancellationToken ct)
    {
        var (record, authorizationError) = await AuthorizedStashAsync(runId, resolutionToken, ct);
        if (record is null)
            return (false, authorizationError, null);

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
            // Refreshed-on-delivery: an OAuth-backed connector never hands out a stale token.
            if (await tokenRefresher.ResolveFreshSettingsAsync(connectorId, ct) is not { } settingsMap)
                return await RejectAsync(runId, "connector-not-found", $"connector '{connectorId}' not found", ct);
            resolvedSettings = settingsMap;
            providerType = binding.ProviderType
                           ?? (await connectors.GetAsync(connectorId, ct))?.ProviderType
                           ?? string.Empty;
        }
        else
        {
            // Inline settings: Secret-kind values were protected when the binding entered the
            // Core — this is the one place they are decrypted, straight into the RSA envelope.
            providerType = binding.ProviderType ?? string.Empty;
            try
            {
                resolvedSettings = await bindingSecrets.UnprotectAsync(binding, ct);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return await RejectAsync(runId, "inline-secret-unreadable",
                    $"an inline secret of slot '{slotName}' cannot be decrypted", ct);
            }
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

/// <summary>A past dispatch's resolution context, as re-gated and re-stashed by a rerun.</summary>
internal sealed record RunResolutionStash(
    IReadOnlyList<SlotBinding> Bindings,
    Guid? TriggeredBy,
    IReadOnlyList<Guid> WorkspaceIds,
    Guid? ConfigurationId);

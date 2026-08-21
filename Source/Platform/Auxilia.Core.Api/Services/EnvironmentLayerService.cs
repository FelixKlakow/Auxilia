using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>A generated environment fragment plus its platform signature (absent without a signing key).</summary>
public sealed record SignedEnvironmentFragment(
    string Fragment, string? SignatureBase64, string? PublisherKeyBase64);

/// <summary>
/// Admin-managed session-environment layers: the Core stores each capability's per-base setup
/// scripts and maintains the matching provider-catalog entry (category "environment",
/// composes-environment, available) so configured workflows can pick it. Runners fetch the
/// fragment for THEIR base at dispatch, token-authorized — a statically configured local layer
/// of the same name stays the host's override. Every mutation is audited.
/// </summary>
public sealed class EnvironmentLayerService(
    IDataAccess<EnvironmentLayerRecord> layers,
    ProviderCatalogService catalog,
    EnvironmentBaseService baseCatalog,
    AuditLog auditLog,
    TimeProvider clock,
    IOptions<CoreApiSettings> settings)
{
    /// <summary>The contract environment providers expose — mirrors the workflow SDK's slot contract.</summary>
    public const string EnvironmentContract = "Auxilia.Workflows.Environment.IExecutionEnvironment";

    public const string Category = "environment";

    public async Task<IReadOnlyList<EnvironmentLayerDto>> ListAsync(string? search, CancellationToken ct)
    {
        var matched = (await layers.ReadAsync(ct))
            .Where(l => Matches(l, search))
            .OrderBy(l => l.ProviderType, StringComparer.Ordinal)
            .ToList();
        var dtos = new List<EnvironmentLayerDto>(matched.Count);
        foreach (var record in matched)
            dtos.Add(await WithGrantsAsync(record, ct));
        return dtos;
    }

    private async Task<EnvironmentLayerDto> WithGrantsAsync(EnvironmentLayerRecord record, CancellationToken ct)
        => ToDto(record) with { Grants = (await catalog.FindAsync(record.ProviderType, ct))?.Grants ?? [] };

    private static bool Matches(EnvironmentLayerRecord record, string? search)
        => string.IsNullOrWhiteSpace(search)
           || Contains(record.ProviderType, search)
           || Contains(record.Description, search)
           || Contains(record.Version, search)
           || ParseVariants(record).Any(v =>
               Contains(v.BaseEnvironment, search) || Contains(v.BaseVersion, search));

    private static bool Contains(string? value, string search)
        => value?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true;

    public async Task<EnvironmentLayerDto?> FindAsync(string providerType, CancellationToken ct)
        => await layers.ReadAsync(EnvironmentLayerRecord.IdFor(providerType), ct) is { } record
            ? await WithGrantsAsync(record, ct)
            : null;

    /// <summary>
    /// The image-build fragment for the runner's on-the-fly composition, generated from the
    /// variant matching the requesting runner's base environment; null when the environment is
    /// unmanaged or has no variant for that base.
    /// </summary>
    public async Task<SignedEnvironmentFragment?> ReadFragmentAsync(
        string providerType, string baseEnvironment, CancellationToken ct)
    {
        var record = await layers.ReadAsync(EnvironmentLayerRecord.IdFor(providerType), ct);
        var variant = record is null ? null : VariantFor(record, baseEnvironment);
        if (variant is null)
            return null;

        // Environments are code the build executes — sign the fragment with the platform key
        // (the same trust anchor as re-signed workflow packages) so runners verify integrity.
        var fragment = string.Equals(
            variant.BaseEnvironment, EnvironmentBases.Windows, StringComparison.OrdinalIgnoreCase)
            ? WindowsFragmentFor(variant.SetupScript)
            : LinuxFragmentFor(variant.SetupScript);
        var pemFile = settings.Value.SigningKeyPemFile;
        if (string.IsNullOrWhiteSpace(pemFile) || !File.Exists(pemFile))
            return new SignedEnvironmentFragment(fragment, null, null);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(await File.ReadAllTextAsync(pemFile, ct));
        var signature = rsa.SignHash(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fragment)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new SignedEnvironmentFragment(
            fragment,
            Convert.ToBase64String(signature),
            Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>
    /// Wraps the setup script into a classic-builder-safe RUN layer: the script travels base64
    /// (no Dockerfile escaping pitfalls), is executed with fail-fast semantics, and leaves no
    /// trace in the image beyond its own effects.
    /// </summary>
    internal static string LinuxFragmentFor(string setupScript)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            setupScript.ReplaceLineEndings("\n")));
        return $"RUN echo {encoded} | base64 -d > /tmp/auxilia-env-setup.sh"
               + " && sh -e /tmp/auxilia-env-setup.sh"
               + " && rm -f /tmp/auxilia-env-setup.sh\n";
    }

    /// <summary>
    /// The Windows twin: the script travels base64 into a PowerShell file executed with
    /// stop-on-error semantics, then removed — same guarantees as the linux fragment.
    /// </summary>
    internal static string WindowsFragmentFor(string setupScript)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            setupScript.ReplaceLineEndings("\r\n")));
        return "RUN powershell -NoProfile -ExecutionPolicy Bypass -Command "
               + "\"$ErrorActionPreference='Stop'; "
               + $"[IO.File]::WriteAllBytes('C:\\auxilia-env-setup.ps1', [Convert]::FromBase64String('{encoded}')); "
               + "& C:\\auxilia-env-setup.ps1; "
               + "Remove-Item -Force C:\\auxilia-env-setup.ps1\"\n";
    }

    /// <summary>
    /// Creates or updates ONE variant of a layer, keyed (providerType, baseEnvironment) — the
    /// layer itself is created on its first variant. The catalog entry is refreshed with the
    /// full base set and made AVAILABLE immediately — the upsert itself is the administrator's
    /// curation act.
    /// </summary>
    public async Task<EnvironmentLayerDto> UpsertAsync(
        Guid? actor, UpsertEnvironmentLayer request, CancellationToken ct)
    {
        var type = request.ProviderType?.Trim() ?? "";
        if (type.Length == 0)
            throw new ArgumentException("providerType is required");
        if (string.IsNullOrWhiteSpace(request.SetupScript))
            throw new ArgumentException(
                "setupScript is required — the script IS how the environment gets ready");
        if (string.IsNullOrWhiteSpace(request.BaseEnvironment))
            throw new ArgumentException("baseEnvironment is required (e.g. linux, windows)");

        // A pinned base version must exist in the base catalog — a pin against a typo would
        // otherwise silently never compose with anything.
        var baseName = request.BaseEnvironment.Trim().ToLowerInvariant();
        var baseVersion = string.IsNullOrWhiteSpace(request.BaseVersion) ? null : request.BaseVersion.Trim();
        if (baseVersion is not null && !await baseCatalog.ExistsAsync(baseName, baseVersion, ct))
            throw new ArgumentException(
                $"base version '{baseName}/{baseVersion}' is not registered in the environment-base catalog");

        var existing = await layers.ReadAsync(EnvironmentLayerRecord.IdFor(type), ct);
        var variants = existing is null ? [] : ParseVariants(existing).ToList();
        variants.RemoveAll(v =>
            string.Equals(v.BaseEnvironment, baseName, StringComparison.OrdinalIgnoreCase));
        variants.Add(new EnvironmentLayerVariant(baseName, request.SetupScript, baseVersion));
        variants.Sort((a, b) => string.CompareOrdinal(a.BaseEnvironment, b.BaseEnvironment));

        var record = new EnvironmentLayerRecord
        {
            Id = EnvironmentLayerRecord.IdFor(type),
            ProviderType = type,
            Description = request.Description,
            VariantsJson = JsonSerializer.Serialize(variants),
            Version = string.IsNullOrWhiteSpace(request.Version) ? null : request.Version.Trim(),
            UpdatedUtc = clock.GetUtcNow(),
            UpdatedBy = actor,
        };
        await layers.SaveAsync(record, ct);
        await RefreshCatalogEntryAsync(actor, record, variants, ct);
        await auditLog.AppendAsync(
            ActorName(actor), "environment-layer.upserted", type, $"upserted variant '{baseName}'", ct: ct);
        return ToDto(record);
    }

    /// <summary>
    /// Removes one base variant; removing the LAST variant removes the layer (and its catalog
    /// entry) — a layer without a single setup script composes nowhere.
    /// </summary>
    public async Task<EnvironmentLayerDto?> RemoveVariantAsync(
        Guid? actor, string providerType, string baseEnvironment, CancellationToken ct)
    {
        var record = await layers.ReadAsync(EnvironmentLayerRecord.IdFor(providerType), ct);
        if (record is null)
            return null;
        var variants = ParseVariants(record).ToList();
        var removed = variants.RemoveAll(v =>
            string.Equals(v.BaseEnvironment, baseEnvironment.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return null;

        if (variants.Count == 0)
        {
            await DeleteAsync(actor, record.ProviderType, ct);
            return ToDto(record) with { Variants = [] };
        }

        var updated = record with
        {
            VariantsJson = JsonSerializer.Serialize(variants),
            UpdatedUtc = clock.GetUtcNow(),
            UpdatedBy = actor,
        };
        await layers.SaveAsync(updated, ct);
        await RefreshCatalogEntryAsync(actor, updated, variants, ct);
        await auditLog.AppendAsync(
            ActorName(actor), "environment-layer.upserted", record.ProviderType,
            $"removed variant '{baseEnvironment.Trim().ToLowerInvariant()}'", ct: ct);
        return ToDto(updated);
    }

    private async Task RefreshCatalogEntryAsync(
        Guid? actor, EnvironmentLayerRecord record, List<EnvironmentLayerVariant> variants,
        CancellationToken ct)
    {
        await catalog.RegisterAsync(ActorName(actor), new RegisterSlotProvider(
            record.ProviderType,
            Category,
            record.Description,
            Contracts: [EnvironmentContract],
            Settings: [],
            ComposesEnvironment: true,
            EnvironmentBases: variants
                .Select(v => new EnvironmentBaseRef(v.BaseEnvironment, v.BaseVersion))
                .ToList()), ct);
        await catalog.SetAvailabilityAsync(ActorName(actor), record.ProviderType, available: true, ct);
    }

    /// <summary>Removes the layer and its catalog entry; configured workflows binding it will fail pre-flight.</summary>
    public async Task<bool> DeleteAsync(Guid? actor, string providerType, CancellationToken ct)
    {
        var removed = await layers.RemoveAsync(EnvironmentLayerRecord.IdFor(providerType), ct);
        if (!removed)
            return false;
        await catalog.DeleteAsync(ActorName(actor), providerType, ct);
        await auditLog.AppendAsync(ActorName(actor), "environment-layer.deleted", providerType, "deleted", ct: ct);
        return true;
    }

    /// <summary>
    /// Replaces who may bind the layer into a run — stored on the layer's CATALOG entry, the one
    /// grant mechanism shared with slot providers (enforced at dispatch). Null when unmanaged.
    /// </summary>
    public async Task<EnvironmentLayerDto?> SetGrantsAsync(
        Guid? actor, string providerType, IReadOnlyList<AccessGrant> grants, CancellationToken ct)
    {
        var record = await layers.ReadAsync(EnvironmentLayerRecord.IdFor(providerType), ct);
        if (record is null)
            return null;
        var entry = await catalog.SetGrantsAsync(ActorName(actor), providerType, grants, ct);
        return ToDto(record) with { Grants = entry.Grants };
    }

    private static string ActorName(Guid? actor) => actor?.ToString("D") ?? "core-api";

    private static EnvironmentLayerVariant? VariantFor(EnvironmentLayerRecord record, string baseEnvironment)
        => ParseVariants(record).FirstOrDefault(v =>
            string.Equals(v.BaseEnvironment, baseEnvironment.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<EnvironmentLayerVariant> ParseVariants(EnvironmentLayerRecord record)
        => string.IsNullOrWhiteSpace(record.VariantsJson)
            ? []
            : JsonSerializer.Deserialize<List<EnvironmentLayerVariant>>(record.VariantsJson) ?? [];

    private static EnvironmentLayerDto ToDto(EnvironmentLayerRecord record)
        => new(record.ProviderType, record.Description, ParseVariants(record),
            record.Version, record.UpdatedUtc);
}

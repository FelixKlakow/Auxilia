using System.Security.Cryptography;
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
/// Admin-managed session-environment layers: the Core stores each capability's Dockerfile
/// fragment and maintains the matching provider-catalog entry (category "environment",
/// composes-environment, available) so configured workflows can pick it. Runners fetch the
/// fragment at dispatch, token-authorized — a statically configured local layer of the same
/// name stays the host's override. Every mutation is audited.
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

    public async Task<IReadOnlyList<EnvironmentLayerDto>> ListAsync(CancellationToken ct)
        => (await layers.ReadAsync(ct))
            .OrderBy(l => l.ProviderType, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();

    public async Task<EnvironmentLayerDto?> FindAsync(string providerType, CancellationToken ct)
        => await layers.ReadAsync(EnvironmentLayerRecord.IdFor(providerType), ct) is { } record
            ? ToDto(record)
            : null;

    /// <summary>
    /// The image-build fragment for the runner's on-the-fly composition, generated from the
    /// environment's setup script; null when the environment is unmanaged or its base cannot be
    /// hosted on the requesting runner's platform (only Linux runners exist today).
    /// </summary>
    public async Task<SignedEnvironmentFragment?> ReadFragmentAsync(string providerType, CancellationToken ct)
    {
        var record = await layers.ReadAsync(EnvironmentLayerRecord.IdFor(providerType), ct);
        if (record is null
            || !string.Equals(record.BaseEnvironment, EnvironmentBases.Linux, StringComparison.OrdinalIgnoreCase))
            return null;

        // Environments are code the build executes — sign the fragment with the platform key
        // (the same trust anchor as re-signed workflow packages) so runners verify integrity.
        var fragment = LinuxFragmentFor(record.SetupScript);
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
    /// Creates or updates an environment and its catalog entry. The entry is made AVAILABLE
    /// immediately — the upsert itself is the administrator's curation act.
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

        var record = new EnvironmentLayerRecord
        {
            Id = EnvironmentLayerRecord.IdFor(type),
            ProviderType = type,
            Description = request.Description,
            BaseEnvironment = baseName,
            BaseVersion = baseVersion,
            SetupScript = request.SetupScript,
            Version = string.IsNullOrWhiteSpace(request.Version) ? null : request.Version.Trim(),
            UpdatedUtc = clock.GetUtcNow(),
            UpdatedBy = actor,
        };
        await layers.SaveAsync(record, ct);

        var actorName = actor?.ToString("D") ?? "core-api";
        await catalog.RegisterAsync(actorName, new RegisterSlotProvider(
            type,
            Category,
            request.Description,
            Contracts: [EnvironmentContract],
            Settings: [],
            ComposesEnvironment: true,
            EnvironmentBase: record.BaseEnvironment,
            EnvironmentBaseVersion: record.BaseVersion), ct);
        await catalog.SetAvailabilityAsync(actorName, type, available: true, ct);
        await auditLog.AppendAsync(actorName, "environment-layer.upserted", type, "upserted", ct: ct);
        return ToDto(record);
    }

    /// <summary>Removes the layer and its catalog entry; configured workflows binding it will fail pre-flight.</summary>
    public async Task<bool> DeleteAsync(Guid? actor, string providerType, CancellationToken ct)
    {
        var removed = await layers.RemoveAsync(EnvironmentLayerRecord.IdFor(providerType), ct);
        if (!removed)
            return false;
        var actorName = actor?.ToString("D") ?? "core-api";
        await catalog.DeleteAsync(actorName, providerType, ct);
        await auditLog.AppendAsync(actorName, "environment-layer.deleted", providerType, "deleted", ct: ct);
        return true;
    }

    private static EnvironmentLayerDto ToDto(EnvironmentLayerRecord record)
        => new(record.ProviderType, record.Description, record.BaseEnvironment,
            record.SetupScript, record.Version, record.UpdatedUtc, record.BaseVersion);
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Crypto;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>Outcome of a registry mutation: the registration view, or a caller error.</summary>
public sealed record RegistryOutcome(WorkflowTypeRegistrationDto? Registration, string? Error)
{
    public static RegistryOutcome Ok(WorkflowTypeRegistrationDto dto) => new(dto, null);
    public static RegistryOutcome Fail(string error) => new(null, error);
}

/// <summary>
/// The Core's workflow-type registry — the deploy-time trust gate of ARCHITECTURE §7. A type is
/// registered permanently (until unregistered) with its signed package coordinate. Trust decides
/// the status: a package signed by a trusted publisher key activates immediately; anything else
/// (untrusted key, or a docker image with no verifiable signature) enters Pending until the
/// signing authority approves — optionally re-signing a Core-stored package with the platform
/// key — or denies with a recorded reason. Only Active types are dispatchable; the runner keeps
/// verifying package integrity, but the <em>authorization</em> to run lives here.
/// </summary>
public sealed class WorkflowTypeRegistryService(
    IDataAccess<CoreWorkflowTypeRecord> store,
    IHttpClientFactory httpClientFactory,
    IOptions<CoreApiSettings> settings,
    AuditLog auditLog,
    TimeProvider clock,
    ILogger<WorkflowTypeRegistryService> logger)
{
    private const string DockerScheme = "docker://";
    private const string CoreScheme = "core://";

    private static readonly JsonSerializerOptions SchemaReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Refuses registrations whose schema declares a structurally invalid companion topology
    /// (unpinned images, cycles, bad bounds) — the registry is the spawn-grant gate, so an
    /// invalid pod must never become registrable. A schema that does not parse is tolerated
    /// here (schema-less and legacy registrations exist); it simply carries no companions.
    /// </summary>
    private static string? ValidateCompanionTopology(string? schemaJson)
    {
        if (schemaJson is not { Length: > 0 })
            return null;
        Auxilia.Workflows.WorkflowSchema? schema;
        try
        {
            schema = JsonSerializer.Deserialize<Auxilia.Workflows.WorkflowSchema>(schemaJson, SchemaReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (schema is null || (schema.Companions.Count == 0 && schema.PodControl is null))
            return null;
        var errors = Auxilia.Workflows.Companions.CompanionTopologyValidator
            .Validate(schema.Companions, schema.PodControl);
        return errors.Count == 0 ? null : "invalid companion topology: " + string.Join(" ", errors);
    }

    public async Task<RegistryOutcome> RegisterAsync(
        RegisterWorkflowTypeRequest request, Guid? registeredBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.WorkflowType))
            return RegistryOutcome.Fail("workflowType is required.");
        var hasUri = !string.IsNullOrWhiteSpace(request.PackageUri);
        var hasUpload = !string.IsNullOrWhiteSpace(request.PackageBase64);
        if (hasUri == hasUpload)
            return RegistryOutcome.Fail("provide exactly one of packageUri or packageBase64.");

        var type = request.WorkflowType.Trim();
        byte[]? packageBytes = null;
        string? packageUri = request.PackageUri?.Trim();
        string? storedPath = null;

        if (hasUpload)
        {
            try
            {
                packageBytes = Convert.FromBase64String(request.PackageBase64!);
            }
            catch (FormatException)
            {
                return RegistryOutcome.Fail("packageBase64 is not valid base64.");
            }
        }
        else if (!packageUri!.StartsWith(DockerScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(packageUri, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return RegistryOutcome.Fail("packageUri must be a docker:// image or an http(s) package URL.");
            try
            {
                packageBytes = await httpClientFactory.CreateClient().GetByteArrayAsync(uri, ct);
            }
            catch (HttpRequestException ex)
            {
                return RegistryOutcome.Fail($"could not download the package: {ex.Message}");
            }
        }

        string status;
        string statusReason;
        string? publisherKey = null;
        string? schemaJson = request.SchemaJson;
        var existing = await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(type), ct);

        if (packageBytes is not null)
        {
            var inspection = WorkflowPackageInspection.Inspect(packageBytes);
            if (!inspection.IsValid)
                return RegistryOutcome.Fail(inspection.Error!);
            if (inspection.WorkflowName is { Length: > 0 } packagedName
                && !string.Equals(packagedName, type, StringComparison.Ordinal))
                return RegistryOutcome.Fail(
                    $"the package declares workflow '{packagedName}', not '{type}'.");

            publisherKey = inspection.PublisherKeyBase64;
            schemaJson = inspection.SchemaJson ?? schemaJson;
            if (IsTrustedPublisher(publisherKey))
            {
                status = WorkflowTypeStatus.Active;
                statusReason = "signed by a trusted publisher key";
            }
            else
            {
                status = WorkflowTypeStatus.Pending;
                statusReason = "the publisher key is not trusted — awaiting the signing authority";
            }
        }
        else
        {
            // A docker image carries no verifiable package signature: trusting it is always an
            // explicit act of the signing authority (or the operator's static host configuration).
            status = WorkflowTypeStatus.Pending;
            statusReason = "a docker package has no verifiable signature — awaiting the signing authority";
            // The Core never inspects images, so without a declared schema the type's whole
            // input/pod surface is INVISIBLE until its first run publishes one — say so where
            // the approver reads, instead of leaving a silent blind spot.
            if (schemaJson is null && existing?.SchemaJson is null)
                statusReason += "; no declared schema — inputs and pod topology stay undiscoverable "
                                + "until the first run publishes one (emit it with --emit-schema and "
                                + "pass schemaJson to declare it up front)";
        }

        if (ValidateCompanionTopology(schemaJson) is { } companionError)
            return RegistryOutcome.Fail(companionError);

        if (hasUpload)
        {
            storedPath = StorePackage(type, packageBytes!);
            packageUri = CoreScheme + type;
        }

        var now = clock.GetUtcNow();
        var record = new CoreWorkflowTypeRecord
        {
            Id = CoreWorkflowTypeRecord.IdFor(type),
            WorkflowType = type,
            PackageUri = packageUri,
            SchemaJson = schemaJson ?? existing?.SchemaJson,
            Status = status,
            StatusReason = statusReason,
            PublisherKeyBase64 = publisherKey,
            StoredPackagePath = storedPath,
            RegisteredBy = registeredBy,
            RegisteredUtc = existing?.RegisteredUtc ?? now,
            UpdatedUtc = now
        };
        await store.SaveAsync(record, ct);
        await auditLog.AppendAsync(
            registeredBy?.ToString() ?? "core-api", "workflow-type.register", type, status,
            statusReason, ct);
        logger.LogInformation(
            "Workflow type {WorkflowType} registered as {Status} ({Reason}).", type, status, statusReason);
        return RegistryOutcome.Ok(ToDto(record));
    }

    /// <summary>Removes a type from the registry (and its stored package). Permanent-until-deleted, per design.</summary>
    public async Task<bool> UnregisterAsync(string workflowType, Guid? actor, CancellationToken ct)
    {
        var record = await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct);
        if (record is null)
            return false;
        await store.RemoveAsync(record.Id, ct);
        if (record.StoredPackagePath is { Length: > 0 } path && File.Exists(path))
            File.Delete(path);
        await auditLog.AppendAsync(
            actor?.ToString() ?? "core-api", "workflow-type.unregister", workflowType, "removed", ct: ct);
        return true;
    }

    /// <summary>
    /// The signing authority accepts a pending registration. A Core-stored package is re-signed
    /// with the platform signing key (when configured) so the platform key becomes the publisher
    /// of record; external packages are activated as-signed — the approval itself is the trust act.
    /// </summary>
    public async Task<RegistryOutcome> ApproveAsync(string workflowType, string actor, CancellationToken ct)
    {
        var record = await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct);
        if (record is null)
            return RegistryOutcome.Fail("workflow type is not registered.");
        if (record.Status == WorkflowTypeStatus.Active)
            return RegistryOutcome.Ok(ToDto(record));

        var publisherKey = record.PublisherKeyBase64;
        var reason = $"approved by {actor}";
        if (record.StoredPackagePath is { Length: > 0 } path && LoadSigningKey() is { } signer)
        {
            var resigned = ResignPackage(await File.ReadAllBytesAsync(path, ct), signer);
            await File.WriteAllBytesAsync(path, resigned.PackageBytes, ct);
            publisherKey = resigned.PublisherKeyBase64;
            reason += " — re-signed with the platform signing key";
        }

        var updated = record with
        {
            Status = WorkflowTypeStatus.Active,
            StatusReason = reason,
            PublisherKeyBase64 = publisherKey,
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(updated, ct);
        await auditLog.AppendAsync(actor, "workflow-type.sign", workflowType, "approved", reason, ct);
        return RegistryOutcome.Ok(ToDto(updated));
    }

    /// <summary>The signing authority refuses a registration; the reason is recorded and surfaced.</summary>
    public async Task<RegistryOutcome> DenyAsync(
        string workflowType, string reason, string actor, CancellationToken ct)
    {
        var record = await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct);
        if (record is null)
            return RegistryOutcome.Fail("workflow type is not registered.");
        var updated = record with
        {
            Status = WorkflowTypeStatus.Denied,
            StatusReason = reason,
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(updated, ct);
        await auditLog.AppendAsync(actor, "workflow-type.sign", workflowType, "denied", reason, ct);
        return RegistryOutcome.Ok(ToDto(updated));
    }

    /// <summary>
    /// Operational on/off switch, distinct from the trust decision: only an Active type can be
    /// disabled, only a Disabled one re-enabled — Pending/Denied stay with the signing authority.
    /// A disabled type stops dispatching (every configured workflow of it becomes unavailable).
    /// </summary>
    public async Task<RegistryOutcome> SetEnabledAsync(
        string workflowType, bool enabled, string actor, CancellationToken ct)
    {
        var record = await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct);
        if (record is null)
            return RegistryOutcome.Fail("workflow type is not registered.");
        if (enabled && record.Status != WorkflowTypeStatus.Disabled)
            return RegistryOutcome.Fail($"only a disabled type can be re-enabled (status: {record.Status}).");
        if (!enabled && record.Status != WorkflowTypeStatus.Active)
            return RegistryOutcome.Fail($"only an active type can be disabled (status: {record.Status}).");

        var updated = record with
        {
            Status = enabled ? WorkflowTypeStatus.Active : WorkflowTypeStatus.Disabled,
            StatusReason = enabled ? $"re-enabled by {actor}" : $"disabled by {actor}",
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(updated, ct);
        await auditLog.AppendAsync(
            actor, enabled ? "workflow-type.enable" : "workflow-type.disable", workflowType,
            updated.Status, updated.StatusReason, ct);
        return RegistryOutcome.Ok(ToDto(updated));
    }

    public async Task<WorkflowTypeRegistrationDto?> GetRegistrationAsync(string workflowType, CancellationToken ct)
        => await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct) is { } record ? ToDto(record) : null;

    public async Task<CoreWorkflowTypeRecord?> GetRecordAsync(string workflowType, CancellationToken ct)
        => await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct);

    /// <summary>
    /// Resolves the package coordinate a run of <paramref name="workflowType"/> dispatches with.
    /// Only Active types resolve; a Core-stored package (<c>core://</c>) is rewritten into a
    /// URL the runner can download, authorized by the run's resolution token.
    /// </summary>
    public async Task<string> ResolvePackageUriForDispatchAsync(
        string workflowType, Guid runId, string resolutionToken, CancellationToken ct)
    {
        var record = await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct)
            ?? throw new KeyNotFoundException($"workflow type '{workflowType}' is not registered");
        if (record.Status != WorkflowTypeStatus.Active)
            throw new InvalidOperationException(
                $"workflow type '{workflowType}' is not active (status: {record.Status}"
                + (record.StatusReason is { Length: > 0 } r ? $" — {r}" : "") + ")");
        if (string.IsNullOrEmpty(record.PackageUri))
            throw new InvalidOperationException($"workflow type '{workflowType}' has no package coordinate");

        if (!record.PackageUri.StartsWith(CoreScheme, StringComparison.OrdinalIgnoreCase))
            return record.PackageUri;

        var baseAddress = settings.Value.PublicBaseAddress?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseAddress))
            throw new InvalidOperationException(
                $"workflow type '{workflowType}' uses a Core-stored package but CoreApi:PublicBaseAddress is not configured");
        return $"{baseAddress}/api/workflow-types/{Uri.EscapeDataString(workflowType)}/package"
               + $"?runId={runId}&token={Uri.EscapeDataString(resolutionToken)}";
    }

    /// <summary>The stored package bytes for the token-authorized download endpoint; null when none.</summary>
    public async Task<byte[]?> ReadStoredPackageAsync(string workflowType, CancellationToken ct)
    {
        var record = await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct);
        if (record?.StoredPackagePath is not { Length: > 0 } path || !File.Exists(path))
            return null;
        return await File.ReadAllBytesAsync(path, ct);
    }

    /// <summary>Refreshes the schema of an already-registered type (runner announcements); unregistered types are ignored.</summary>
    public async Task<bool> UpdateSchemaAsync(string workflowType, string schemaJson, CancellationToken ct)
    {
        var record = await store.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct);
        if (record is null)
            return false;
        await store.SaveAsync(record with { SchemaJson = schemaJson, UpdatedUtc = clock.GetUtcNow() }, ct);
        return true;
    }

    /// <summary>Idempotently seeds a static (operator-configured) type as Active; an existing type is left untouched.</summary>
    public async Task EnsureSeededAsync(StaticWorkflowType seed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seed.WorkflowType))
            return;
        var id = CoreWorkflowTypeRecord.IdFor(seed.WorkflowType);
        if (await store.ReadAsync(id, ct) is not null)
            return;
        var now = clock.GetUtcNow();
        await store.SaveAsync(new CoreWorkflowTypeRecord
        {
            Id = id,
            WorkflowType = seed.WorkflowType,
            PackageUri = seed.PackageUri,
            SchemaJson = seed.SchemaJson,
            Status = WorkflowTypeStatus.Active,
            StatusReason = "seeded from host configuration",
            RegisteredUtc = now,
            UpdatedUtc = now
        }, ct);
    }

    private bool IsTrustedPublisher(string? publisherKeyBase64)
    {
        if (string.IsNullOrEmpty(publisherKeyBase64))
            return false;
        if (settings.Value.TrustedPublisherKeys.Contains(publisherKeyBase64, StringComparer.Ordinal))
            return true;
        // The platform's own signing key is always trusted (a previously approved, re-signed package).
        return LoadSigningKey() is { } signer && signer.PublicKeyBase64 == publisherKeyBase64;
    }

    private string StorePackage(string workflowType, byte[] packageBytes)
    {
        var directory = Path.GetFullPath(settings.Value.PackageStoreDirectory);
        Directory.CreateDirectory(directory);
        // The type name is untrusted input — never let it shape the path.
        var path = Path.Combine(directory, DeterministicGuid.For("workflow-package", workflowType).ToString("N") + ".workflow.zip");
        File.WriteAllBytes(path, packageBytes);
        return path;
    }

    private SigningKey? LoadSigningKey()
    {
        var pemFile = settings.Value.SigningKeyPemFile;
        if (string.IsNullOrWhiteSpace(pemFile) || !File.Exists(pemFile))
            return null;
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(pemFile));
        return new SigningKey(rsa);
    }

    /// <summary>
    /// Re-signs a package with the platform key: file hashes are recomputed from the actual
    /// entries, the manifest gets the platform public key, and a fresh RSA-PSS signature.
    /// </summary>
    private static (byte[] PackageBytes, string PublisherKeyBase64) ResignPackage(byte[] packageBytes, SigningKey signer)
    {
        using var signerScope = signer;
        using var buffer = new MemoryStream();
        buffer.Write(packageBytes);
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            var manifestEntry = archive.GetEntry("package-manifest.json")
                ?? throw new InvalidOperationException("package-manifest.json not found in the package.");
            WorkflowPackageManifest manifest;
            using (var manifestStream = manifestEntry.Open())
                manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
                    manifestStream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("package-manifest.json is not deserializable.");

            var files = manifest.Files.Select(f =>
            {
                var entry = archive.GetEntry(f.FileName)
                    ?? throw new InvalidOperationException($"manifest references missing entry '{f.FileName}'.");
                using var entryStream = entry.Open();
                using var bytes = new MemoryStream();
                entryStream.CopyTo(bytes);
                return f with { HashBase64 = Convert.ToBase64String(SHA256.HashData(bytes.ToArray())) };
            }).ToList();

            var unsigned = manifest with
            {
                Files = files,
                SignatureBase64 = string.Empty,
                PublicKeyBase64 = signer.PublicKeyBase64
            };
            var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, WorkflowPackageJsonOptions.SerializeOptions);
            var signed = unsigned with { SignatureBase64 = Convert.ToBase64String(signer.Sign(unsignedBytes)) };

            manifestEntry.Delete();
            var newEntry = archive.CreateEntry("package-manifest.json");
            using var newStream = newEntry.Open();
            newStream.Write(JsonSerializer.SerializeToUtf8Bytes(signed, WorkflowPackageJsonOptions.SerializeOptions));
        }

        return (buffer.ToArray(), signer.PublicKeyBase64);
    }

    private static WorkflowTypeRegistrationDto ToDto(CoreWorkflowTypeRecord record) => new(
        record.WorkflowType,
        record.PackageUri,
        record.Status,
        record.StatusReason,
        record.PublisherKeyBase64,
        record.StoredPackagePath is { Length: > 0 },
        record.RegisteredBy,
        record.RegisteredUtc,
        record.UpdatedUtc);

    /// <summary>The platform signing keypair loaded from PEM (hash-in / signature-out, disposed after use).</summary>
    private sealed class SigningKey(RSA rsa) : IDisposable
    {
        public string PublicKeyBase64 { get; } = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        public byte[] Sign(byte[] data)
            => rsa.SignHash(SHA256.HashData(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        public void Dispose() => rsa.Dispose();
    }
}

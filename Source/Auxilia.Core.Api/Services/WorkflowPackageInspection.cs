using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Workflows.Crypto;

namespace Auxilia.Core.Api.Services;

/// <summary>What the registry learned from inspecting a workflow package ZIP.</summary>
public sealed record PackageInspectionResult(
    bool IsValid,
    string? Error,
    string? PublisherKeyBase64,
    string? SchemaJson,
    string? WorkflowName);

/// <summary>
/// Inspects a workflow package ZIP for registration: verifies every file hash against the
/// manifest and the manifest's RSA-PSS signature (internal consistency — the same check the
/// runner performs), and extracts the publisher key + the packed <c>workflow-schema.json</c>.
/// The <em>trust</em> decision (is the publisher key accepted) is the registry's, not this class's.
/// </summary>
public static class WorkflowPackageInspection
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    public static PackageInspectionResult Inspect(byte[] packageBytes)
    {
        try
        {
            using var stream = new MemoryStream(packageBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var manifestEntry = archive.GetEntry("package-manifest.json");
            if (manifestEntry is null)
                return Invalid("package-manifest.json not found in the package.");

            WorkflowPackageManifest? manifest;
            using (var manifestStream = manifestEntry.Open())
                manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(manifestStream, DeserializeOptions);
            if (manifest is null)
                return Invalid("package-manifest.json is not deserializable.");

            foreach (var fileEntry in manifest.Files)
            {
                var entry = archive.GetEntry(fileEntry.FileName);
                if (entry is null)
                    return Invalid($"manifest references missing entry '{fileEntry.FileName}'.");
                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                entryStream.CopyTo(buffer);
                if (!SHA256.HashData(buffer.ToArray()).SequenceEqual(Convert.FromBase64String(fileEntry.HashBase64)))
                    return Invalid($"hash mismatch for entry '{fileEntry.FileName}'.");
            }

            if (string.IsNullOrEmpty(manifest.SignatureBase64) || string.IsNullOrEmpty(manifest.PublicKeyBase64))
                return Invalid("the package manifest is unsigned.");

            var unsignedManifest = manifest with { SignatureBase64 = string.Empty };
            var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsignedManifest, WorkflowPackageJsonOptions.SerializeOptions);
            var manifestHash = SHA256.HashData(unsignedBytes);

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(manifest.PublicKeyBase64), out _);
            if (!rsa.VerifyHash(manifestHash, Convert.FromBase64String(manifest.SignatureBase64),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                return Invalid("the package manifest signature is invalid.");

            string? schemaJson = null;
            string? workflowName = null;
            if (archive.GetEntry("workflow-schema.json") is { } schemaEntry)
            {
                using var schemaStream = new StreamReader(schemaEntry.Open());
                schemaJson = schemaStream.ReadToEnd();
                using var doc = JsonDocument.Parse(schemaJson);
                if (doc.RootElement.TryGetProperty("WorkflowName", out var name)
                    || doc.RootElement.TryGetProperty("workflowName", out name))
                    workflowName = name.GetString();
            }

            return new PackageInspectionResult(true, null, manifest.PublicKeyBase64, schemaJson, workflowName);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or CryptographicException)
        {
            return Invalid($"the package is not a valid signed workflow ZIP: {ex.Message}");
        }
    }

    private static PackageInspectionResult Invalid(string error) => new(false, error, null, null, null);
}

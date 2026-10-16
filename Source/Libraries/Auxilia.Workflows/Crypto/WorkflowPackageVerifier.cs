using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows.Crypto;

public sealed class WorkflowPackageVerifier(
    IDeveloperModeProvider developerMode,
    ILogger<WorkflowPackageVerifier> logger,
    IReadOnlyCollection<string>? trustedPublisherKeys = null) : IWorkflowPackageVerifier
{
    internal static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool Verify(Stream zipStream)
    {
        if (developerMode.IsActive)
        {
            logger.LogWarning("Workflow package verification bypassed (developer mode)");
            return true;
        }

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = archive.GetEntry("package-manifest.json")
            ?? throw new InvalidOperationException("package-manifest.json not found in ZIP.");

        WorkflowPackageManifest manifest;
        using (var manifestStream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(manifestStream, DeserializeOptions)
                ?? throw new InvalidOperationException("Failed to deserialise package-manifest.json.");
        }

        // Only what the signed manifest lists may ride in the package: an unlisted entry would be
        // extracted next to the signed files and executed or read as if it were part of them.
        var listed = new HashSet<string>(manifest.Files.Select(f => f.FileName), StringComparer.Ordinal)
        {
            "package-manifest.json"
        };
        var unlisted = archive.Entries
            .Where(e => !e.FullName.EndsWith('/') && !listed.Contains(e.FullName))
            .Select(e => e.FullName)
            .ToList();
        if (unlisted.Count > 0)
        {
            logger.LogWarning("Workflow package rejected: entries not covered by the signed manifest: {Entries}",
                string.Join(", ", unlisted.Take(5)));
            return false;
        }

        // With trusted publisher keys configured, a self-consistent signature is not enough —
        // it must be by one of them (the platform signing key or an approved publisher).
        if (trustedPublisherKeys is { Count: > 0 } && !trustedPublisherKeys.Contains(manifest.PublicKeyBase64))
        {
            logger.LogWarning("Workflow package rejected: its publisher key is not trusted by this runner.");
            return false;
        }

        foreach (var fileEntry in manifest.Files)
        {
            var entry = archive.GetEntry(fileEntry.FileName)
                ?? throw new InvalidOperationException($"ZIP entry '{fileEntry.FileName}' not found.");

            byte[] actualHash;
            using (var entryStream = entry.Open())
            {
                using var ms = new MemoryStream();
                entryStream.CopyTo(ms);
                actualHash = SHA256.HashData(ms.ToArray());
            }

            var expectedHash = Convert.FromBase64String(fileEntry.HashBase64);
            if (!actualHash.SequenceEqual(expectedHash))
                return false;
        }

        var unsignedManifest = manifest with { SignatureBase64 = string.Empty };
        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsignedManifest, WorkflowPackageJsonOptions.SerializeOptions);
        var manifestHash = SHA256.HashData(unsignedBytes);

        var signatureBytes = Convert.FromBase64String(manifest.SignatureBase64);
        var publicKeyBytes = Convert.FromBase64String(manifest.PublicKeyBase64);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
        return rsa.VerifyHash(manifestHash, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}

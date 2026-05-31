using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows.Crypto;

public sealed class WorkflowPackageVerifier(
    IDeveloperModeProvider developerMode,
    ILogger<WorkflowPackageVerifier> logger) : IWorkflowPackageVerifier
{
    internal static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
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
        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsignedManifest, SerializeOptions);
        var manifestHash = SHA256.HashData(unsignedBytes);

        var signatureBytes = Convert.FromBase64String(manifest.SignatureBase64);
        var publicKeyBytes = Convert.FromBase64String(manifest.PublicKeyBase64);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
        return rsa.VerifyHash(manifestHash, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}

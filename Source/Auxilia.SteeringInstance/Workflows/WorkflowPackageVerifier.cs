using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Verifies a workflow ZIP package by checking the RSA-PSS signature declared in
/// <c>manifest.json</c>.  In developer mode the signature check is skipped so that
/// packages produced by the test harness (which may not be signed) can still run.
/// </summary>
public class WorkflowPackageVerifier(
    IDeveloperModeProvider developerMode,
    ILogger<WorkflowPackageVerifier> logger)
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads <c>manifest.json</c> from <paramref name="archive"/> and verifies its RSA-PSS
    /// signature.  Returns <see langword="true"/> when the package is trusted.
    /// </summary>
    /// <remarks>Virtual so Moq can override it in unit tests.</remarks>
    public virtual bool Verify(ZipArchive archive)
    {
        if (developerMode.IsActive)
        {
            logger.LogWarning(
                "Developer mode active — skipping workflow package signature verification.");
            return true;
        }

        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry is null)
        {
            logger.LogError("Workflow package is missing manifest.json.");
            return false;
        }

        WorkflowPackageManifest manifest;
        try
        {
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(stream, ManifestOptions)
                       ?? throw new InvalidOperationException("manifest.json deserialized to null.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deserialize manifest.json.");
            return false;
        }

        try
        {
            var publicKeyBytes = Convert.FromBase64String(manifest.PublicKeyBase64);
            var signatureBytes = Convert.FromBase64String(manifest.SignatureBase64);
            var contentHashBytes = Convert.FromBase64String(manifest.ContentHashBase64);

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

            var isValid = rsa.VerifyData(
                contentHashBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);

            if (!isValid)
                logger.LogError("Workflow package signature verification failed.");

            return isValid;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during workflow package signature verification.");
            return false;
        }
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows.Crypto;

public sealed class PluginManifestVerifier(
    IDeveloperModeProvider developerMode,
    ILogger<PluginManifestVerifier> logger) : IPluginManifestVerifier
{
    public bool Verify(PluginManifest manifest, byte[] assemblyBytes)
    {
        if (developerMode.IsActive)
        {
            logger.LogWarning(
                "Plugin manifest verification bypassed for {ProviderType} (developer mode)",
                manifest.ProviderType);
            return true;
        }

        var actualHash = SHA256.HashData(assemblyBytes);
        var expectedHash = Convert.FromBase64String(manifest.ContentHashBase64);
        if (!actualHash.SequenceEqual(expectedHash))
            return false;

        var signatureBytes = Convert.FromBase64String(manifest.SignatureBase64);
        var publicKeyBytes = Convert.FromBase64String(manifest.PublicKeyBase64);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
        return rsa.VerifyHash(actualHash, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}

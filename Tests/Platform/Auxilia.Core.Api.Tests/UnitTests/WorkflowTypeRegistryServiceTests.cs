using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// The workflow-type registry's trust decisions: a trusted-publisher signature activates, an
/// untrusted one (or a docker image) pends, invalid packages are rejected, approval optionally
/// re-signs a Core-stored package with the platform key, and dispatch resolves Active types only.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkflowTypeRegistryServiceTests
{
    private string _tempDir = null!;
    private RSA _publisherKey = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "auxilia-registry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _publisherKey = RSA.Create(2048);
    }

    [TearDown]
    public void TearDown()
    {
        _publisherKey.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string PublisherKeyBase64 => Convert.ToBase64String(_publisherKey.ExportSubjectPublicKeyInfo());

    private WorkflowTypeRegistryService NewService(
        CoreApiSettings? settings = null, Func<HttpRequestMessage, HttpResponseMessage>? http = null)
    {
        settings ??= new CoreApiSettings();
        settings.PackageStoreDirectory = Path.Combine(_tempDir, "packages");
        return new WorkflowTypeRegistryService(
            new InMemoryDataAccess<CoreWorkflowTypeRecord>(),
            new StubHttpClientFactory(new StubHttpMessageHandler(
                http ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)))),
            Options.Create(settings),
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System),
            TimeProvider.System,
            NullLogger<WorkflowTypeRegistryService>.Instance);
    }

    /// <summary>A minimal signed workflow package: one payload file + schema + signed manifest.</summary>
    private byte[] BuildSignedPackage(string workflowName, RSA? signWith = null, bool corruptPayload = false)
    {
        var rsa = signWith ?? _publisherKey;
        var payload = Encoding.UTF8.GetBytes("payload");
        var schemaJson = JsonSerializer.Serialize(
            new WorkflowSchema(workflowName, [], []) { Version = "1.0.0" },
            WorkflowPackageJsonOptions.SerializeOptions);
        var schemaBytes = Encoding.UTF8.GetBytes(schemaJson);

        var files = new List<WorkflowPackageFileEntry>
        {
            new("wf.exe", Convert.ToBase64String(SHA256.HashData(payload))),
            new("workflow-schema.json", Convert.ToBase64String(SHA256.HashData(schemaBytes)))
        };
        var unsigned = new WorkflowPackageManifest(
            files, string.Empty,
            Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), "wf.exe");
        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, WorkflowPackageJsonOptions.SerializeOptions);
        var signature = rsa.SignHash(
            SHA256.HashData(unsignedBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var manifest = unsigned with { SignatureBase64 = Convert.ToBase64String(signature) };

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] bytes)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
            Add("wf.exe", corruptPayload ? Encoding.UTF8.GetBytes("tampered") : payload);
            Add("workflow-schema.json", schemaBytes);
            Add("package-manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(manifest, WorkflowPackageJsonOptions.SerializeOptions));
        }
        return buffer.ToArray();
    }

    [Test]
    public async Task TrustedPublisherPackage_ActivatesImmediately_AndExtractsTheSchema()
    {
        var service = NewService(new CoreApiSettings { TrustedPublisherKeys = { PublisherKeyBase64 } });
        var package = BuildSignedPackage("trusted-wf");

        var outcome = await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("trusted-wf", PackageBase64: Convert.ToBase64String(package)),
            registeredBy: Guid.NewGuid(), CancellationToken.None);

        Assert.That(outcome.Error, Is.Null);
        Assert.That(outcome.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Active));
        Assert.That(outcome.Registration.PublisherKeyBase64, Is.EqualTo(PublisherKeyBase64));
        var record = await service.GetRecordAsync("trusted-wf", CancellationToken.None);
        Assert.That(record!.SchemaJson, Does.Contain("trusted-wf"),
            "the packed workflow-schema.json becomes the type's schema at registration");
    }

    [Test]
    public async Task UntrustedSignature_Pends_UntilApproved()
    {
        var service = NewService();
        var package = BuildSignedPackage("pending-wf");

        var outcome = await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("pending-wf", PackageBase64: Convert.ToBase64String(package)),
            null, CancellationToken.None);
        Assert.That(outcome.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Pending));

        var approved = await service.ApproveAsync("pending-wf", "signer", CancellationToken.None);
        Assert.That(approved.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Active));
    }

    [Test]
    public async Task TamperedPackage_IsRejected()
    {
        var service = NewService();
        var package = BuildSignedPackage("evil-wf", corruptPayload: true);

        var outcome = await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("evil-wf", PackageBase64: Convert.ToBase64String(package)),
            null, CancellationToken.None);

        Assert.That(outcome.Registration, Is.Null);
        Assert.That(outcome.Error, Does.Contain("hash mismatch"));
    }

    [Test]
    public async Task PackagedNameMustMatchTheRegisteredType()
    {
        var service = NewService();
        var package = BuildSignedPackage("actual-name");

        var outcome = await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("claimed-name", PackageBase64: Convert.ToBase64String(package)),
            null, CancellationToken.None);

        Assert.That(outcome.Registration, Is.Null);
        Assert.That(outcome.Error, Does.Contain("actual-name"));
    }

    [Test]
    public async Task HttpPackage_IsDownloadedAndVerified()
    {
        var package = BuildSignedPackage("http-wf");
        var service = NewService(
            new CoreApiSettings { TrustedPublisherKeys = { PublisherKeyBase64 } },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });

        var outcome = await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("http-wf", "https://packages.example/http-wf.workflow.zip"),
            null, CancellationToken.None);

        Assert.That(outcome.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Active));
        Assert.That(outcome.Registration.PackageUri, Is.EqualTo("https://packages.example/http-wf.workflow.zip"),
            "an externally hosted package keeps its external coordinate");
    }

    [Test]
    public async Task Approve_ResignsAStoredPackage_WithThePlatformKey()
    {
        // The platform signing key on disk (PEM) — the Trust Service of ARCHITECTURE §7.
        using var platformKey = RSA.Create(2048);
        var pemPath = Path.Combine(_tempDir, "signing.pem");
        await File.WriteAllTextAsync(pemPath, platformKey.ExportRSAPrivateKeyPem());
        var platformPublic = Convert.ToBase64String(platformKey.ExportSubjectPublicKeyInfo());

        var service = NewService(new CoreApiSettings { SigningKeyPemFile = pemPath });
        var package = BuildSignedPackage("resign-wf");
        var outcome = await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("resign-wf", PackageBase64: Convert.ToBase64String(package)),
            null, CancellationToken.None);
        Assert.That(outcome.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Pending));
        Assert.That(outcome.Registration.HasStoredPackage, Is.True);
        Assert.That(outcome.Registration.PackageUri, Is.EqualTo("core://resign-wf"));

        var approved = await service.ApproveAsync("resign-wf", "signer", CancellationToken.None);

        Assert.That(approved.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Active));
        Assert.That(approved.Registration.PublisherKeyBase64, Is.EqualTo(platformPublic),
            "the platform key becomes the publisher of record");
        var stored = await service.ReadStoredPackageAsync("resign-wf", CancellationToken.None);
        var inspection = WorkflowPackageInspection.Inspect(stored!);
        Assert.That(inspection.IsValid, Is.True, inspection.Error);
        Assert.That(inspection.PublisherKeyBase64, Is.EqualTo(platformPublic),
            "the stored package is genuinely re-signed, not just re-labeled");
    }

    [Test]
    public async Task Deny_RecordsTheReason()
    {
        var service = NewService();
        await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("bad-wf", "docker://bad:1"), null, CancellationToken.None);

        var denied = await service.DenyAsync("bad-wf", "unsafe network use", "signer", CancellationToken.None);

        Assert.That(denied.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Denied));
        Assert.That(denied.Registration.StatusReason, Is.EqualTo("unsafe network use"));
    }

    [Test]
    public async Task ResolvePackageUri_RewritesCoreScheme_ToATokenAuthorizedUrl()
    {
        var service = NewService(new CoreApiSettings
        {
            TrustedPublisherKeys = { PublisherKeyBase64 },
            PublicBaseAddress = "http://core:8080"
        });
        var package = BuildSignedPackage("stored-wf");
        await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("stored-wf", PackageBase64: Convert.ToBase64String(package)),
            null, CancellationToken.None);

        var runId = Guid.NewGuid();
        var uri = await service.ResolvePackageUriForDispatchAsync(
            "stored-wf", runId, "tok-123", CancellationToken.None);

        Assert.That(uri, Is.EqualTo(
            $"http://core:8080/api/workflow-types/stored-wf/package?runId={runId}&token=tok-123"));
    }

    [Test]
    public async Task Disable_SwitchesAnActiveTypeOff_AndBlocksDispatch()
    {
        var service = NewService(new CoreApiSettings { TrustedPublisherKeys = { PublisherKeyBase64 } });
        var package = BuildSignedPackage("switchable-wf");
        await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("switchable-wf", PackageBase64: Convert.ToBase64String(package)),
            null, CancellationToken.None);

        var disabled = await service.SetEnabledAsync("switchable-wf", enabled: false, "admin", CancellationToken.None);

        Assert.That(disabled.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Disabled));
        Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolvePackageUriForDispatchAsync(
            "switchable-wf", Guid.NewGuid(), "tok", CancellationToken.None),
            "a disabled type must not dispatch — its configured workflows become unavailable");
    }

    [Test]
    public async Task Enable_RestoresADisabledType_ToActive()
    {
        var service = NewService(new CoreApiSettings { TrustedPublisherKeys = { PublisherKeyBase64 } });
        var package = BuildSignedPackage("switchable-wf");
        await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("switchable-wf", PackageBase64: Convert.ToBase64String(package)),
            null, CancellationToken.None);
        await service.SetEnabledAsync("switchable-wf", enabled: false, "admin", CancellationToken.None);

        var enabled = await service.SetEnabledAsync("switchable-wf", enabled: true, "admin", CancellationToken.None);

        Assert.That(enabled.Registration!.Status, Is.EqualTo(WorkflowTypeStatus.Active));
        Assert.That(enabled.Registration.StatusReason, Is.EqualTo("re-enabled by admin"));
    }

    [Test]
    public async Task EnabledSwitch_NeverTouchesTrustStates()
    {
        var service = NewService();
        await service.RegisterAsync(
            new RegisterWorkflowTypeRequest("pending-wf", "docker://img:1"), null, CancellationToken.None);

        var disablePending = await service.SetEnabledAsync("pending-wf", enabled: false, "admin", CancellationToken.None);
        var enablePending = await service.SetEnabledAsync("pending-wf", enabled: true, "admin", CancellationToken.None);
        var unknown = await service.SetEnabledAsync("ghost", enabled: false, "admin", CancellationToken.None);

        Assert.That(disablePending.Error, Does.Contain("only an active type"));
        Assert.That(enablePending.Error, Does.Contain("only a disabled type"));
        Assert.That(unknown.Error, Does.Contain("not registered"));
    }

    [Test]
    public async Task SchemaAnnouncements_NeverCreateATypeOnlyRefreshIt()
    {
        var service = NewService();
        var updatedGhost = await service.UpdateSchemaAsync("ghost", "{}", CancellationToken.None);
        Assert.That(updatedGhost, Is.False);

        await service.EnsureSeededAsync(
            new StaticWorkflowType { WorkflowType = "seeded", PackageUri = "docker://x" }, CancellationToken.None);
        var updatedSeeded = await service.UpdateSchemaAsync(
            "seeded", "{\"workflowName\":\"seeded\"}", CancellationToken.None);
        Assert.That(updatedSeeded, Is.True);
    }
}

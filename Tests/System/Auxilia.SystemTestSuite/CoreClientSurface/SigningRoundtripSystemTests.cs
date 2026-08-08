using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// The signed-package trust roundtrip over the REAL network — signing is unit-tested in
/// <c>WorkflowTypeRegistryServiceTests</c>; this fixture proves the same trust boundary holds
/// end to end: package bytes ride the Core API, the Core stores/serves them, and the RUNNER
/// independently re-verifies before any launch.
/// <para>Covered:</para>
/// <list type="bullet">
/// <item>Upload signed with the configured trusted publisher key → auto-Active with a
///     <c>core://</c> coordinate, and a dispatch makes the runner token-download the Core-served
///     package and PASS signature verification (the run proceeds to the launch stage).</item>
/// <item>Upload signed with an unknown key → Pending; approval re-signs the stored package so
///     the platform key becomes the publisher of record, and the re-signed package still passes
///     the runner's verification at dispatch.</item>
/// <item>A tampered upload is REFUSED at registration (hash mismatch).</item>
/// <item>An externally hosted package whose bytes are swapped for tampered ones AFTER
///     registration fails its run at the runner's verification — the exact supply-chain attack
///     the runner-side check exists for.</item>
/// </list>
/// <para>Deliberately NOT covered: actually EXECUTING a zip-packaged workflow. A zip launch
/// bind-mounts the extracted package from the runner's own filesystem, but in this environment
/// the runner is itself a container talking to the HOST's Docker daemon — the daemon cannot
/// resolve the runner-local extraction path, so the payload never reaches the workflow
/// container. The dispatch tests therefore prove a PASSED verification via the runner's
/// package-extracted log line — emitted strictly between signature verification and launch,
/// and never on the refusal path — not via run completion; docker-image runs complete for real
/// in the sibling fixtures. The fixture teardown best-effort-removes any launch container a
/// verified dispatch created but could never start.</para>
/// </summary>
[TestFixture]
[Category("System")]
public sealed class SigningRoundtripSystemTests
{
    private const string PackageServerAlias = "signing-packages";
    private const string PackageServerRoot = "/usr/share/nginx/html";

    private static ICoreClient Admin => _admin ??= CoreClientEnvironment.CreateClient();
    private static ICoreClient? _admin;

    private IContainer _packageServer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // A plain static file server on the test network: the Core downloads from it at
        // registration, the runner downloads from it at dispatch — and the test swaps the
        // served bytes in between (the mutable-source scenario).
        _packageServer = new ContainerBuilder("nginx:1.27-alpine")
            .WithNetwork(CoreClientEnvironment.Network)
            .WithNetworkAliases(PackageServerAlias)
            .WithPortBinding(80, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(80).ForPath("/")))
            .Build();
        await _packageServer.StartAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await RemoveStrandedLaunchContainersAsync();
        if (_packageServer is not null)
            await _packageServer.DisposeAsync();
    }

    /// <summary>
    /// Best-effort removal of the containers this fixture's verified-package dispatches CREATED
    /// but could never START (see the fixture doc). Scoped hard: only workflow-labeled containers
    /// in status <c>created</c> from the system-test dummy image are touched — running Auxilia
    /// containers (this suite's or any dev stack's) never match.
    /// </summary>
    private static async Task RemoveStrandedLaunchContainersAsync()
    {
        try
        {
            using var docker = new Docker.DotNet.DockerClientConfiguration().CreateClient();
            var stranded = await docker.Containers.ListContainersAsync(new Docker.DotNet.Models.ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { ["auxilia.workflow=1"] = true },
                    ["status"] = new Dictionary<string, bool> { ["created"] = true },
                    ["ancestor"] = new Dictionary<string, bool>
                    {
                        [CoreApiDispatch.CoreApiDispatchEnvironment.DummyWorkflowsImageName] = true
                    }
                }
            });
            foreach (var container in stranded)
                await docker.Containers.RemoveContainerAsync(
                    container.ID, new Docker.DotNet.Models.ContainerRemoveParameters { Force = true });
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync($"(stranded-container cleanup skipped: {ex.Message})");
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task TrustedUpload_ActivatesImmediately_AndTheRunnerVerifiesTheCoreServedPackage(CancellationToken ct)
    {
        const string type = "signing-trusted-wf";
        var package = BuildSignedPackage(type, CoreClientEnvironment.TrustedPublisherRsa);

        var registered = await Admin.RegisterWorkflowTypeAsync(
            new RegisterWorkflowTypeRequest(type, PackageBase64: Convert.ToBase64String(package)), ct);
        Assert.Multiple(() =>
        {
            Assert.That(registered.Status, Is.EqualTo("Active"),
                "a trusted publisher signature must activate without the signing authority");
            Assert.That(registered.PublisherKeyBase64, Is.EqualTo(CoreClientEnvironment.TrustedPublisherKeyBase64));
            Assert.That(registered.PackageUri, Is.EqualTo($"core://{type}"),
                "an uploaded package becomes Core-stored and Core-served");
            Assert.That(registered.HasStoredPackage, Is.True);
        });

        try
        {
            // Dispatch: the Core rewrites core:// into a run-token-authorized download URL, the
            // runner fetches it over the network and verifies the signature BEFORE launching.
            // The zip payload itself cannot execute here (see the fixture doc), so the proof of
            // a PASSED verification is the runner's package-extracted log line — emitted only
            // AFTER a successful download + verification, and never after a failed one.
            await Admin.RunAsync(new RunRequest(type), ct);
            await AssertRunnerVerifiedAsync(type, ct);
        }
        finally
        {
            await Admin.UnregisterWorkflowTypeAsync(type, CancellationToken.None);
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task UntrustedUpload_Pends_AndApproval_ResignsWithThePlatformKey(CancellationToken ct)
    {
        const string type = "signing-pending-wf";
        using var unknownPublisher = RSA.Create(2048);
        var package = BuildSignedPackage(type, unknownPublisher);

        var registered = await Admin.RegisterWorkflowTypeAsync(
            new RegisterWorkflowTypeRequest(type, PackageBase64: Convert.ToBase64String(package)), ct);
        Assert.Multiple(() =>
        {
            Assert.That(registered.Status, Is.EqualTo("Pending"),
                "an unknown publisher key must await the signing authority");
            Assert.That(registered.PublisherKeyBase64,
                Is.EqualTo(Convert.ToBase64String(unknownPublisher.ExportSubjectPublicKeyInfo())));
        });

        try
        {
            var approved = await Admin.ApproveWorkflowTypeAsync(type, ct);
            Assert.Multiple(() =>
            {
                Assert.That(approved.Status, Is.EqualTo("Active"));
                Assert.That(approved.PublisherKeyBase64, Is.EqualTo(CoreClientEnvironment.PlatformPublicKeyBase64),
                    "approval must re-sign the stored package — the platform key becomes the publisher of record");
            });

            // The RE-SIGNED package must still verify on the runner: a broken re-sign would
            // surface right here as a verification failure instead of the extracted line.
            await Admin.RunAsync(new RunRequest(type), ct);
            await AssertRunnerVerifiedAsync(type, ct);
        }
        finally
        {
            await Admin.UnregisterWorkflowTypeAsync(type, CancellationToken.None);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task TamperedUpload_IsRefusedAtRegistration(CancellationToken ct)
    {
        const string type = "signing-tampered-wf";
        var package = BuildSignedPackage(type, CoreClientEnvironment.TrustedPublisherRsa, corruptPayload: true);

        var refusal = Assert.ThrowsAsync<CoreApiException>(() => Admin.RegisterWorkflowTypeAsync(
            new RegisterWorkflowTypeRequest(type, PackageBase64: Convert.ToBase64String(package)), ct))!;
        Assert.That(refusal.ErrorDetail, Does.Contain("hash mismatch"),
            "a payload not matching its signed manifest must never register — even under a trusted key");
        Assert.That(await Admin.GetWorkflowTypeRegistrationAsync(type, ct), Is.Null,
            "a refused registration must leave no registry record");
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task PackageSwappedAfterRegistration_FailsTheRun_AtTheRunnerVerification(CancellationToken ct)
    {
        const string type = "signing-swapped-wf";
        var servedPath = $"{PackageServerRoot}/{type}.workflow.zip";
        var packageUrl = $"http://{PackageServerAlias}/{type}.workflow.zip";

        // Registration downloads GENUINE bytes from the external server → trusted → Active.
        // The external coordinate is kept, so every dispatch re-downloads from the source.
        await _packageServer.CopyAsync(
            BuildSignedPackage(type, CoreClientEnvironment.TrustedPublisherRsa), servedPath);
        var registered = await Admin.RegisterWorkflowTypeAsync(
            new RegisterWorkflowTypeRequest(type, PackageUri: packageUrl), ct);
        Assert.Multiple(() =>
        {
            Assert.That(registered.Status, Is.EqualTo("Active"));
            Assert.That(registered.PackageUri, Is.EqualTo(packageUrl),
                "an externally hosted package keeps its external coordinate");
        });

        try
        {
            // The supply-chain attack: the source swaps the bytes AFTER the trust decision.
            await _packageServer.CopyAsync(
                BuildSignedPackage(type, CoreClientEnvironment.TrustedPublisherRsa, corruptPayload: true),
                servedPath);

            var accepted = await Admin.RunAsync(new RunRequest(type), ct);
            await ClientStreamProbe.AwaitTerminalAsync(Admin, accepted.RunId, ct);
            var run = await ClientStreamProbe.GetRunResolvedAsync(Admin, accepted, ct);
            Assert.Multiple(() =>
            {
                Assert.That(run.State, Is.EqualTo("PreFlightFailed"),
                    "the tampered package must never reach a container launch");
                Assert.That(run.Error, Does.Contain("signature verification failed"),
                    "the runner's own verification — not the Core's registration check — must catch the swap");
            });
        }
        finally
        {
            await Admin.UnregisterWorkflowTypeAsync(type, CancellationToken.None);
        }
    }

    /// <summary>
    /// Asserts the runner network-downloaded and signature-VERIFIED the type's package: the
    /// dispatcher logs "Workflow package extracted" strictly between verification and launch,
    /// and "verification failed" on the refusal path — the only observable seam, because a
    /// verified zip payload cannot execute in this topology (see the fixture doc).
    /// </summary>
    private static async Task AssertRunnerVerifiedAsync(string workflowType, CancellationToken ct)
    {
        var extracted = $"Workflow package extracted. WorkflowType={workflowType}";
        var log = string.Empty;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (!log.Contains(extracted) && DateTimeOffset.UtcNow <= deadline)
        {
            await Task.Delay(500, ct);
            log = await CoreClientEnvironment.GetRunnerLogAsync(ct);
        }
        Assert.Multiple(() =>
        {
            Assert.That(log, Does.Contain(extracted),
                "the runner must have downloaded AND verified the package before extracting it");
            Assert.That(log, Does.Not.Contain($"verification failed for {workflowType}"),
                "the genuine package must never trip the runner's verifier");
        });
    }

    /// <summary>
    /// A minimal signed workflow package (payload + schema + RSA-PSS-signed manifest) — the same
    /// shape <c>WorkflowPackageInspection</c> and the runner's verifier check in production.
    /// </summary>
    private static byte[] BuildSignedPackage(string workflowName, RSA signWith, bool corruptPayload = false)
    {
        var payload = Encoding.UTF8.GetBytes("system-test payload — not executable");
        // PascalCase on purpose: the Core's schema READ side deserializes stored schema JSON
        // with case-SENSITIVE defaults, so a camelCase schema (what WorkflowPacker emits) breaks
        // dispatch-time schema reads — a latent product bug outside this test's scope.
        var schemaBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new WorkflowSchema(workflowName, [], []) { Version = "1.0.0" }));

        var files = new List<WorkflowPackageFileEntry>
        {
            new("wf.exe", Convert.ToBase64String(SHA256.HashData(payload))),
            new("workflow-schema.json", Convert.ToBase64String(SHA256.HashData(schemaBytes)))
        };
        var unsigned = new WorkflowPackageManifest(
            files, string.Empty,
            Convert.ToBase64String(signWith.ExportSubjectPublicKeyInfo()), "wf.exe");
        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, WorkflowPackageJsonOptions.SerializeOptions);
        var signature = signWith.SignHash(
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
            Add("wf.exe", corruptPayload ? Encoding.UTF8.GetBytes("tampered payload") : payload);
            Add("workflow-schema.json", schemaBytes);
            Add("package-manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(manifest, WorkflowPackageJsonOptions.SerializeOptions));
        }
        return buffer.ToArray();
    }
}

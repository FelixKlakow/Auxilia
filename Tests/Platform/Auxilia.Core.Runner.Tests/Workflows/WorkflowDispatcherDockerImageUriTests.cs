using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowDispatcherDockerImageUriTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<IWorkflowLauncher> _mockLauncher = null!;
    private Mock<IWorkflowPackageVerifier> _mockVerifier = null!;
    private Mock<PendingWorkflowPackageStore> _mockPendingPackages = null!;
    private Mock<IHttpClientFactory> _mockHttpFactory = null!;
    private Func<RunWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private WorkflowDispatcher _sut = null!;
    private FakeBrokerIdentityIssuer _brokerIssuer = null!;

    private static DockerWorkflowLauncherSettings DefaultLauncherSettings() => new()
    {
        NetworkName    = "test-net",
        RabbitMqHost   = "rabbitmq",
        RabbitMqPort   = 5672
    };

    private WorkflowDispatcher BuildDispatcher(
        DockerWorkflowLauncherSettings? launcherSettings = null,
        SlotProviderRegistry? providerRegistry = null,
        IHttpClientFactory? httpFactory = null,
        WorkflowDispatcherSettings? dispatcherSettings = null,
        string? daemonOs = null)
    {
        var disposable = new Mock<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.DeclareQueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // The dispatcher publishes a WorkflowStatusEvent for every received command.
        _mockBus
            .Setup(b => b.DeclareTopicExchangeAsync("workflow.status", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishToTopicExchangeAsync(
                "workflow.status", It.IsAny<string>(), It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockBus
            .Setup(b => b.SubscribeAsync<RunWorkflowCommand>(
                It.IsAny<string>(),
                It.IsAny<Func<RunWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<RunWorkflowCommand, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(disposable.Object);

        return new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(launcherSettings ?? DefaultLauncherSettings()),
            Options.Create(dispatcherSettings ?? new WorkflowDispatcherSettings { ContainerExitGraceSeconds = 0 }),
            httpFactory ?? _mockHttpFactory.Object,
            _mockVerifier.Object,
            _mockPendingPackages.Object,
            providerRegistry ?? TestStores.NewSlotProviderRegistry(),
            new WorkflowInstanceTokenRegistry(
                Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System),
            TestStores.NewPolicyEngine(),
            TestStores.NewWorkflowInstanceRegistry(),
            TestStores.NewStatusPublisher(_mockBus.Object),
            TestStores.NewWorkflowSchemaStore(),
            TestStores.NewWorkflowPackageStore(),
            TestStores.NewArtifactStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(),
            daemonOs is null ? TestStores.NewHostPlatformProbe() : HostPlatformReporting(daemonOs),
            Mock.Of<IRepositoryAuthResolver>(),
            TestStores.NewAuditLog(),
            TestStores.NewInstanceInfo(),
            new Auxilia.PlatformData.Protection.NullSettingsProtector(),
            new FakePodHost(),
            new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            TestStores.NewContainment(_brokerIssuer),
            NullLogger<WorkflowDispatcher>.Instance);
    }

    private static RunnerHostPlatformProbe HostPlatformReporting(string os)
    {
        var system = new Mock<Docker.DotNet.ISystemOperations>();
        system.Setup(s => s.GetSystemInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Docker.DotNet.Models.SystemInfoResponse { OSType = os, Architecture = "x86_64" });
        var client = new Mock<Docker.DotNet.IDockerClient>();
        client.SetupGet(c => c.System).Returns(system.Object);
        var factory = new Mock<IDockerClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string?>())).Returns(client.Object);
        return new RunnerHostPlatformProbe(factory.Object, Options.Create(new DockerWorkflowLauncherSettings()),
            NullLogger<RunnerHostPlatformProbe>.Instance);
    }

    private async Task<WorkflowStatusEvent?> DispatchOnAsync(WorkflowDispatcher dispatcher, string packageUri)
    {
        await _sut.StopAsync();
        _sut = dispatcher;
        await _sut.StartAsync(CancellationToken.None);
        WorkflowStatusEvent? failed = null;
        _mockBus
            .Setup(b => b.PublishToTopicExchangeAsync(
                "workflow.status", It.IsAny<string>(), It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorkflowStatusEvent, CancellationToken>((_, _, e, _) =>
            {
                if (e.State == "PreFlightFailed")
                    failed = e;
            })
            .Returns(Task.CompletedTask);
        await _capturedHandler!(new RunWorkflowCommand(
            Guid.NewGuid(), "wf", packageUri, new Dictionary<string, string>()), CancellationToken.None);
        return failed;
    }

    [Test]
    public async Task ASignatureTrustingRunner_RefusesADockerTag_AndRunsADigestPinnedImage()
    {
        var trusting = new WorkflowDispatcherSettings { ContainerExitGraceSeconds = 0, TrustedSigningKeys = ["key"] };

        var refused = await DispatchOnAsync(BuildDispatcher(dispatcherSettings: trusting), "docker://img:1");
        Assert.That(refused?.ErrorMessage, Does.Contain("not pinned by digest"));

        var pinned = await DispatchOnAsync(BuildDispatcher(dispatcherSettings: trusting),
            "docker://img@sha256:" + new string('a', 64));
        Assert.That(pinned, Is.Null);
    }

    [Test]
    public async Task ADaemonThatCannotEnforceEgress_RunsNothing_UntilTheOperatorAcceptsIt()
    {
        var refused = await DispatchOnAsync(BuildDispatcher(daemonOs: "windows"), "docker://img:1");
        Assert.That(refused?.ErrorMessage, Does.Contain("AllowUnenforcedEgress"));
        _mockLauncher.Verify(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        var accepted = DefaultLauncherSettings();
        accepted.AllowUnenforcedEgress = true;
        var allowAll = await DispatchOnAsync(BuildDispatcher(launcherSettings: accepted, daemonOs: "windows"),
            "docker://img:1");
        Assert.That(allowAll?.ErrorMessage, Does.Contain("only execute allow-all runs"),
            "even when accepted, a non-allow-all run is never launched without enforcement");
    }

    [SetUp]
    public async Task SetUp()
    {
        _mockBus            = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _mockLauncher       = new Mock<IWorkflowLauncher>(MockBehavior.Strict);
        _mockVerifier       = new Mock<IWorkflowPackageVerifier>();
        _mockPendingPackages = new Mock<PendingWorkflowPackageStore>();
        _mockHttpFactory    = new Mock<IHttpClientFactory>();
        _brokerIssuer       = new FakeBrokerIdentityIssuer();

        _mockVerifier.Setup(v => v.Verify(It.IsAny<Stream>())).Returns(true);
        _mockPendingPackages.Setup(p => p.Store(It.IsAny<Guid>(), It.IsAny<string>()));
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowLaunchResult());

        _sut = BuildDispatcher();
        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown() => await _sut.StopAsync();

    // -----------------------------------------------------------------------

    [Test]
    public async Task HandleAsync_Launch_HandsTheContainerItsOwnScopedBrokerIdentity()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        await _capturedHandler!(new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "docker://img:1", new Dictionary<string, string>()), CancellationToken.None);

        var instanceId = Guid.Parse(captured!.EnvironmentVariables["Workflow__InstanceId"]);
        var user = WorkloadBusIdentity.UserNameFor(instanceId);
        Assert.That(captured.EnvironmentVariables["RabbitMq__UserName"], Is.EqualTo(user));
        Assert.That(captured.EnvironmentVariables["RabbitMq__Password"], Is.EqualTo($"pw-{user}"));
        Assert.That(_brokerIssuer.Active.Keys, Is.EquivalentTo(new[] { user }));
        Assert.That(_brokerIssuer.Active[user],
            Is.EqualTo(Auxilia.Workflows.Messaging.WorkloadBusAccess.For(instanceId, WorkloadBusTopology.IngressQueues(new WorkflowDispatcherSettings()))).Using<BrokerPermissions>(
                (a, b) => a.Configure == b.Configure && a.Write == b.Write && a.Read == b.Read));
    }

    [Test]
    public async Task HandleAsync_LaunchFailure_RevokesTheIssuedIdentity()
    {
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("docker down"));

        await _capturedHandler!(new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "docker://img:1", new Dictionary<string, string>()), CancellationToken.None);

        Assert.That(_brokerIssuer.Active, Is.Empty, "a run that never started must not leave a live credential");
        Assert.That(_brokerIssuer.Revoked, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task HandleAsync_ContainerExit_RevokesTheIdentity()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());
        await _capturedHandler!(new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "docker://img:1", new Dictionary<string, string>()), CancellationToken.None);
        Assert.That(_brokerIssuer.Active, Has.Count.EqualTo(1));

        await captured!.OnExited!(new ContainerExit(0, null));

        Assert.That(_brokerIssuer.Active, Is.Empty);
    }

    [Test]
    public async Task HandleAsync_DockerUri_SetsDockerImageUri_AndSkipsHttpDownload()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "docker://my-image:1.0",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DockerImageUri, Is.EqualTo("my-image:1.0"));
        Assert.That(string.IsNullOrEmpty(captured.ExtractedContentDirectory), Is.True);

        // No HTTP call should have been made
        _mockHttpFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task HandleAsync_DockerUri_SlotPluginFilesStillPopulated()
    {
        var providerRegistry = TestStores.NewSlotProviderRegistry();
        await providerRegistry.UpsertAsync("MyProvider", "/plugins/my-provider.slothandler.dll");

        await _sut.StopAsync();
        _sut = BuildDispatcher(providerRegistry: providerRegistry);
        await _sut.StartAsync(CancellationToken.None);

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "docker://my-image:1.0",
            new Dictionary<string, string>(),
            SlotProviderTypes: new[] { "MyProvider" });

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.SlotPluginFiles, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task HandleAsync_HttpUri_DockerImageUriIsNull()
    {
        var packageZip = CreateMinimalPackageZip();
        var httpFactory = CreateHttpClientFactory(packageZip);

        await _sut.StopAsync();
        _sut = BuildDispatcher(httpFactory: httpFactory);
        await _sut.StartAsync(CancellationToken.None);

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DockerImageUri, Is.Null);
    }

    [Test]
    public async Task HandleAsync_ExtraEnvironmentVariables_AreMergedIntoRequest()
    {
        var launcherSettings = DefaultLauncherSettings();
        launcherSettings.ExtraEnvironmentVariables = new Dictionary<string, string>
        {
            ["AUXILIA_DEVELOPER_MODE"] = "1"
        };

        await _sut.StopAsync();
        _sut = BuildDispatcher(launcherSettings);
        await _sut.StartAsync(CancellationToken.None);

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "docker://my-image:1.0",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.EnvironmentVariables.ContainsKey("AUXILIA_DEVELOPER_MODE"), Is.True);
        Assert.That(captured.EnvironmentVariables["AUXILIA_DEVELOPER_MODE"], Is.EqualTo("1"));
    }

    // -----------------------------------------------------------------------

    private static byte[] CreateMinimalPackageZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("package-manifest.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(JsonSerializer.Serialize(new
            {
                files = Array.Empty<object>(),
                signatureBase64 = "c2ln",
                publicKeyBase64 = "a2V5",
                executableRelativePath = "bin/my-workflow"
            }));
        }
        return ms.ToArray();
    }

    private static IHttpClientFactory CreateHttpClientFactory(byte[] responseBytes)
    {
        var handler = new StubHttpMessageHandler(responseBytes);
        var client  = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("workflow-packages")).Returns(client);
        return factory.Object;
    }

    private sealed class StubHttpMessageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
    }
}

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

    private static DockerWorkflowLauncherSettings DefaultLauncherSettings() => new()
    {
        NetworkName    = "test-net",
        RabbitMqHost   = "rabbitmq",
        RabbitMqPort   = 5672,
        RabbitMqUserName = "guest",
        RabbitMqPassword = "guest"
    };

    private WorkflowDispatcher BuildDispatcher(
        DockerWorkflowLauncherSettings? launcherSettings = null,
        SlotProviderRegistry? providerRegistry = null,
        IHttpClientFactory? httpFactory = null)
    {
        var disposable = new Mock<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.DeclareQueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // The dispatcher publishes a WorkflowStatusEvent for every received command.
        _mockBus
            .Setup(b => b.DeclareExchangeAsync("workflow.status-events", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishToExchangeAsync(
                "workflow.status-events", It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
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
            Options.Create(new WorkflowDispatcherSettings()),
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
            TestStores.NewAuditLog(),
            TestStores.NewInstanceInfo(),
            NullLogger<WorkflowDispatcher>.Instance);
    }

    [SetUp]
    public async Task SetUp()
    {
        _mockBus            = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _mockLauncher       = new Mock<IWorkflowLauncher>(MockBehavior.Strict);
        _mockVerifier       = new Mock<IWorkflowPackageVerifier>();
        _mockPendingPackages = new Mock<PendingWorkflowPackageStore>();
        _mockHttpFactory    = new Mock<IHttpClientFactory>();

        _mockVerifier.Setup(v => v.Verify(It.IsAny<Stream>())).Returns(true);
        _mockPendingPackages.Setup(p => p.Store(It.IsAny<string>(), It.IsAny<string>()));
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

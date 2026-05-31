using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowDispatcherTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<IWorkflowLauncher> _mockLauncher = null!;
    private Mock<IWorkflowPackageVerifier> _mockVerifier = null!;
    private Mock<PendingWorkflowPackageStore> _mockPendingPackages = null!;
    private Func<RunWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private WorkflowDispatcher _sut = null!;
    private byte[] _validPackageZip = null!;

    private static DockerWorkflowLauncherSettings DefaultSettings() => new()
    {
        NetworkName = "test-net",
        RabbitMqHost = "rabbitmq",
        RabbitMqPort = 5672,
        RabbitMqUserName = "guest",
        RabbitMqPassword = "guest"
    };

    private static byte[] CreateMinimalPackageZip(string workflowType = "my-workflow")
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
        var client = new HttpClient(handler);
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

    [SetUp]
    public async Task SetUp()
    {
        _validPackageZip = CreateMinimalPackageZip();

        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _mockLauncher = new Mock<IWorkflowLauncher>(MockBehavior.Strict);
        _mockVerifier = new Mock<IWorkflowPackageVerifier>();
        _mockPendingPackages = new Mock<PendingWorkflowPackageStore>();

        _mockVerifier.Setup(v => v.Verify(It.IsAny<Stream>())).Returns(true);
        _mockPendingPackages.Setup(p => p.Store(It.IsAny<string>(), It.IsAny<string>()));

        var disposable = new Mock<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.DeclareQueueAsync("workflow.run-commands", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockBus
            .Setup(b => b.SubscribeAsync<RunWorkflowCommand>(
                "workflow.run-commands",
                It.IsAny<Func<RunWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<RunWorkflowCommand, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(disposable.Object);

        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(DefaultSettings()),
            Options.Create(new WorkflowDispatcherSettings()),
            CreateHttpClientFactory(_validPackageZip),
            _mockVerifier.Object,
            _mockPendingPackages.Object,
            new SlotConfigurationStore(),
            NullLogger<WorkflowDispatcher>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown() => await _sut.StopAsync();

    [Test]
    public void WhenStartCalled_DeclaresRunCommandsQueue()
    {
        _mockBus.Verify(
            b => b.DeclareQueueAsync("workflow.run-commands", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void WhenStartCalled_SubscribesToRunCommandsQueue()
    {
        _mockBus.Verify(
            b => b.SubscribeAsync<RunWorkflowCommand>(
                "workflow.run-commands",
                It.IsAny<Func<RunWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenRunCommandReceived_CallsLauncherExactlyOnce()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        _mockLauncher.Verify(l => l.LaunchAsync(
            It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenRunCommandReceived_PassesExtractedPathToLauncher()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.ExtractedContentDirectory, Does.Contain("auxilia-wf-"));
        Assert.That(Directory.Exists(captured.ExtractedContentDirectory), Is.True);
    }

    [Test]
    public async Task WhenRunCommandReceived_InjectsRabbitMqEnvVars()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "https://example.com/test.workflow.zip", new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(captured!.EnvironmentVariables["RabbitMq__Host"],     Is.EqualTo("rabbitmq"));
            Assert.That(captured!.EnvironmentVariables["RabbitMq__Port"],     Is.EqualTo("5672"));
            Assert.That(captured!.EnvironmentVariables["RabbitMq__UserName"], Is.EqualTo("guest"));
            Assert.That(captured!.EnvironmentVariables["RabbitMq__Password"], Is.EqualTo("guest"));
        });
    }

    [Test]
    public async Task WhenRunCommandHasContext_ForwardsContextAsUpperCaseEnvVars()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>
            {
                ["repo_url"] = "https://github.com/example/repo",
                ["branch"]   = "main"
            });

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        var env = captured!.EnvironmentVariables;
        Assert.That(env.ContainsKey("WORKFLOW_CONTEXT__REPO_URL"), Is.True);
        Assert.That(env["WORKFLOW_CONTEXT__REPO_URL"], Is.EqualTo("https://github.com/example/repo"));
        Assert.That(env.ContainsKey("WORKFLOW_CONTEXT__BRANCH"), Is.True);
        Assert.That(env["WORKFLOW_CONTEXT__BRANCH"], Is.EqualTo("main"));
    }

    [Test]
    public async Task WhenRunCommandHasContext_OriginalKeysDoNotAppearWithoutPrefix()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "https://example.com/test.workflow.zip",
            new Dictionary<string, string> { ["MY_KEY"] = "value" });

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured!.EnvironmentVariables.ContainsKey("MY_KEY"), Is.False);
    }

    [Test]
    public async Task WhenRunCommandReceived_InjectsContextEnvVars()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "https://example.com/test.workflow.zip",
            new Dictionary<string, string> { ["TaskId"] = "42" });

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.EnvironmentVariables["WORKFLOW_CONTEXT__TASKID"], Is.EqualTo("42"));
    }

    [Test]
    public async Task WhenPackageVerificationFails_DoesNotLaunch()
    {
        _mockVerifier.Setup(v => v.Verify(It.IsAny<Stream>())).Returns(false);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        _mockLauncher.Verify(
            l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task WhenRunCommandReceived_RegistersPendingPackage()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        _mockPendingPackages.Verify(
            p => p.Store("my-workflow", It.Is<string>(s => s.Contains("auxilia-wf-"))),
            Times.Once);
    }

    // ------------------------------------------------------------------ SlotPluginFile enrichment

    [Test]
    public async Task Enrichment_MatchFound_SlotPluginFilesHasOneEntry()
    {
        var slotStore = new SlotConfigurationStore();
        slotStore.UpsertConfiguration("my-workflow",
            new StoredSlotConfiguration("slot1", "MyProvider",
                new Dictionary<string, string>(), ConfigurationStatus.Valid));

        var settings = DefaultSettings();
        settings.SlotPackages["MyProvider"] = "/plugins/my-provider.slothandler.dll";

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        await _sut.StopAsync();
        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(settings),
            Options.Create(new WorkflowDispatcherSettings()),
            CreateHttpClientFactory(_validPackageZip),
            _mockVerifier.Object,
            _mockPendingPackages.Object,
            slotStore,
            NullLogger<WorkflowDispatcher>.Instance);
        await _sut.StartAsync(CancellationToken.None);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());
        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.SlotPluginFiles, Has.Count.EqualTo(1));
        Assert.That(captured.SlotPluginFiles[0].DllPath, Is.EqualTo("/plugins/my-provider.slothandler.dll"));
    }

    [Test]
    public async Task Enrichment_NoSlotConfig_SlotPluginFilesIsEmpty()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());
        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.SlotPluginFiles, Is.Empty);
    }

    [Test]
    public async Task Enrichment_ProviderTypeAbsentFromSlotPackages_SlotPluginFilesIsEmpty()
    {
        var slotStore = new SlotConfigurationStore();
        slotStore.UpsertConfiguration("my-workflow",
            new StoredSlotConfiguration("slot1", "UnknownProvider",
                new Dictionary<string, string>(), ConfigurationStatus.Valid));

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        await _sut.StopAsync();
        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(DefaultSettings()),
            Options.Create(new WorkflowDispatcherSettings()),
            CreateHttpClientFactory(_validPackageZip),
            _mockVerifier.Object,
            _mockPendingPackages.Object,
            slotStore,
            NullLogger<WorkflowDispatcher>.Instance);
        await _sut.StartAsync(CancellationToken.None);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());
        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.SlotPluginFiles, Is.Empty);
    }

    [Test]
    public async Task Enrichment_DuplicateProviderType_DeduplicatesToOneEntry()
    {
        var slotStore = new SlotConfigurationStore();
        slotStore.UpsertConfiguration("my-workflow",
            new StoredSlotConfiguration("slot1", "MyProvider",
                new Dictionary<string, string>(), ConfigurationStatus.Valid));
        slotStore.UpsertConfiguration("my-workflow",
            new StoredSlotConfiguration("slot2", "MyProvider",
                new Dictionary<string, string>(), ConfigurationStatus.Valid));

        var settings = DefaultSettings();
        settings.SlotPackages["MyProvider"] = "/plugins/my-provider.slothandler.dll";

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        await _sut.StopAsync();
        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(settings),
            Options.Create(new WorkflowDispatcherSettings()),
            CreateHttpClientFactory(_validPackageZip),
            _mockVerifier.Object,
            _mockPendingPackages.Object,
            slotStore,
            NullLogger<WorkflowDispatcher>.Instance);
        await _sut.StartAsync(CancellationToken.None);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());
        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.SlotPluginFiles, Has.Count.EqualTo(1));
    }
}

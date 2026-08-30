using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

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
    private WorkflowInstanceTokenRegistry _tokenRegistry = null!;
    private WorkflowSchemaStore _schemaStore = null!;
    private WorkflowInstanceRegistry _instanceRegistry = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;

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
        _tokenRegistry = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System);
        _schemaStore = TestStores.NewWorkflowSchemaStore();
        _instanceRegistry = TestStores.NewWorkflowInstanceRegistry();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();

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

        // The dispatcher pre-creates the per-instance response queue at launch.
        _mockBus
            .Setup(b => b.DeclareQueueAsync(
                It.Is<string>(q => q.StartsWith("workflow-response-")), It.IsAny<CancellationToken>()))
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
                "workflow.run-commands",
                It.IsAny<Func<RunWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<RunWorkflowCommand, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(disposable.Object);

        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowLaunchResult());

        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(DefaultSettings()),
            Options.Create(new WorkflowDispatcherSettings { ContainerExitGraceSeconds = 0 }),
            CreateHttpClientFactory(_validPackageZip),
            _mockVerifier.Object,
            _mockPendingPackages.Object,
            TestStores.NewSlotProviderRegistry(),
            _tokenRegistry,
            TestStores.NewPolicyEngine(),
            _instanceRegistry,
            TestStores.NewStatusPublisher(_mockBus.Object),
            _schemaStore,
            TestStores.NewWorkflowPackageStore(),
            TestStores.NewArtifactStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(),
            TestStores.NewHostPlatformProbe(),
            Mock.Of<IRepositoryAuthResolver>(),
            new AuditLog(_auditRecords, TimeProvider.System),
            TestStores.NewInstanceInfo(),
            new Auxilia.PlatformData.Protection.NullSettingsProtector(),
            new FakePodHost(),
            new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            NullLogger<WorkflowDispatcher>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync();
        _auditRecords.Dispose();
    }

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
            .ReturnsAsync(new WorkflowLaunchResult());

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
            .ReturnsAsync(new WorkflowLaunchResult());

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
            .ReturnsAsync(new WorkflowLaunchResult());

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
            .ReturnsAsync(new WorkflowLaunchResult());

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
            .ReturnsAsync(new WorkflowLaunchResult());

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
        var providerRegistry = TestStores.NewSlotProviderRegistry();
        await providerRegistry.UpsertAsync("MyProvider", "/plugins/my-provider.slothandler.dll");

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        await _sut.StopAsync();
        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(DefaultSettings()),
            Options.Create(new WorkflowDispatcherSettings { ContainerExitGraceSeconds = 0 }),
            CreateHttpClientFactory(_validPackageZip),
            _mockVerifier.Object,
            _mockPendingPackages.Object,
            providerRegistry,
            _tokenRegistry,
            TestStores.NewPolicyEngine(),
            _instanceRegistry,
            TestStores.NewStatusPublisher(_mockBus.Object),
            _schemaStore,
            TestStores.NewWorkflowPackageStore(),
            TestStores.NewArtifactStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(),
            TestStores.NewHostPlatformProbe(),
            Mock.Of<IRepositoryAuthResolver>(),
            new AuditLog(_auditRecords, TimeProvider.System),
            TestStores.NewInstanceInfo(),
            new Auxilia.PlatformData.Protection.NullSettingsProtector(),
            new FakePodHost(),
            new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            NullLogger<WorkflowDispatcher>.Instance);
        await _sut.StartAsync(CancellationToken.None);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>(),
            SlotProviderTypes: new[] { "MyProvider" });
        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.SlotPluginFiles, Has.Count.EqualTo(1));
        Assert.That(captured.SlotPluginFiles[0].DllPath, Is.EqualTo("/plugins/my-provider.slothandler.dll"));
    }

    [Test]
    public async Task Enrichment_NoSlotProviderTypes_SlotPluginFilesIsEmpty()
    {
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
        Assert.That(captured!.SlotPluginFiles, Is.Empty);
    }

    [Test]
    public async Task Enrichment_ProviderTypeUnregistered_FailsPreFlightAndDoesNotLaunch()
    {
        // The default dispatcher's provider registry is empty, so an unregistered provider
        // type on the command fails pre-flight — no container is launched.
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>(),
            SlotProviderTypes: new[] { "UnknownProvider" });
        await _capturedHandler!(command, CancellationToken.None);

        _mockLauncher.Verify(
            l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockBus.Verify(
            b => b.PublishToTopicExchangeAsync(
                "workflow.status",
                It.IsAny<string>(),
                It.Is<WorkflowStatusEvent>(e => e.State == "PreFlightFailed"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Enrichment_DuplicateProviderType_DeduplicatesToOneEntry()
    {
        var providerRegistry = TestStores.NewSlotProviderRegistry();
        await providerRegistry.UpsertAsync("MyProvider", "/plugins/my-provider.slothandler.dll");

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        await _sut.StopAsync();
        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(DefaultSettings()),
            Options.Create(new WorkflowDispatcherSettings { ContainerExitGraceSeconds = 0 }),
            CreateHttpClientFactory(_validPackageZip),
            _mockVerifier.Object,
            _mockPendingPackages.Object,
            providerRegistry,
            _tokenRegistry,
            TestStores.NewPolicyEngine(),
            _instanceRegistry,
            TestStores.NewStatusPublisher(_mockBus.Object),
            _schemaStore,
            TestStores.NewWorkflowPackageStore(),
            TestStores.NewArtifactStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(),
            TestStores.NewHostPlatformProbe(),
            Mock.Of<IRepositoryAuthResolver>(),
            new AuditLog(_auditRecords, TimeProvider.System),
            TestStores.NewInstanceInfo(),
            new Auxilia.PlatformData.Protection.NullSettingsProtector(),
            new FakePodHost(),
            new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            NullLogger<WorkflowDispatcher>.Instance);
        await _sut.StartAsync(CancellationToken.None);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>(),
            SlotProviderTypes: new[] { "MyProvider", "MyProvider" });
        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.SlotPluginFiles, Has.Count.EqualTo(1));
    }

    // ------------------------------------------------------------------ Instance token issuance

    [Test]
    public async Task WhenRunCommandReceived_InjectsInstanceIdAndTokenEnvVars()
    {
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
        var env = captured!.EnvironmentVariables;
        Assert.That(env.ContainsKey(WorkflowEnvironmentVariables.InstanceId), Is.True);
        Assert.That(env.ContainsKey(WorkflowEnvironmentVariables.InstanceToken), Is.True);
        Assert.That(Guid.TryParse(env[WorkflowEnvironmentVariables.InstanceId], out _), Is.True);
        Assert.That(env[WorkflowEnvironmentVariables.InstanceToken], Is.Not.Empty);
        Assert.That(env[WorkflowEnvironmentVariables.AnnouncementQueue], Is.EqualTo("workflow.announcements"));
    }

    [Test]
    public async Task WhenRunCommandReceived_IssuedTokenValidatesInRegistry()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        var env = captured!.EnvironmentVariables;
        var instanceId = Guid.Parse(env[WorkflowEnvironmentVariables.InstanceId]);
        var token = env[WorkflowEnvironmentVariables.InstanceToken];
        Assert.That(_tokenRegistry.Validate(instanceId, token), Is.True);
    }

    [Test]
    public async Task WhenRunCommandReceived_PreDeclaresInstanceResponseQueue()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        var instanceId = Guid.Parse(captured!.EnvironmentVariables[WorkflowEnvironmentVariables.InstanceId]);
        _mockBus.Verify(
            b => b.DeclareQueueAsync(WorkflowQueues.ResponseQueueFor(instanceId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------ Network policy

    [Test]
    public async Task WhenRunCommandReceived_ResolvedNetworkPolicyIsPassedToLaunchRequest()
    {
        await _schemaStore.SetSchemaAsync("my-workflow",
            new WorkflowSchema("my-workflow", [], [])
            {
                NetworkEndpoints = [new NetworkEndpointDeclaration("api.nuget.org", "NuGet restore")]
            });

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
        Assert.That(captured!.NetworkPolicy, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(captured.NetworkPolicy!.Mode, Is.EqualTo(NetworkPolicyMode.DefaultDeny));
            Assert.That(captured.NetworkPolicy.AllowedEndpoints, Is.EqualTo(new[] { "api.nuget.org" }));
        });
    }

    [Test]
    public async Task WhenRunCommandReceived_NetworkPolicyIsAudited()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string> { ["NetworkAllow"] = "internal-api.corp.local" });

        await _capturedHandler!(command, CancellationToken.None);

        var instanceId = captured!.EnvironmentVariables[WorkflowEnvironmentVariables.InstanceId];
        var entries = (await _auditRecords.ReadAsync())
            .Where(r => r.Action == "workflow.network-policy").ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Subject, Is.EqualTo(instanceId));
            Assert.That(entries[0].Outcome, Is.EqualTo("DefaultDeny"));
            Assert.That(entries[0].DetailJson, Does.Contain("internal-api.corp.local"));
        });
    }

    [Test]
    public async Task WhenRunCommandReceived_InjectsResourceProxyQueueEnvVar()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured!.EnvironmentVariables[WorkflowEnvironmentVariables.ResourceProxyQueue],
            Is.EqualTo("workflow-resource-proxy"));
    }

    [Test]
    public async Task ContainerExit_WithoutTerminalState_FailsTheRun()
    {
        var instanceId = Guid.NewGuid();
        await _instanceRegistry.CreateAsync(instanceId, "my-workflow", "Queued");

        await _sut.HandleContainerExitAsync(instanceId, "my-workflow", new ContainerExit(139, "segfault at 0x0"));

        var record = await _instanceRegistry.GetAsync(instanceId);
        Assert.That(record!.State, Is.EqualTo("Failed"),
            "a crashed container must fail its run — never leave it stuck in Queued/Running");
        Assert.That(record.ErrorMessage, Does.Contain("exited (code 139)").And.Contain("segfault"));
        _mockBus.Verify(b => b.PublishToTopicExchangeAsync(
            "workflow.status",
            It.IsAny<string>(),
            It.Is<WorkflowStatusEvent>(e => e.WorkflowInstanceId == instanceId && e.State == "Failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ContainerExit_AfterTerminalState_ChangesNothing()
    {
        var instanceId = Guid.NewGuid();
        await _instanceRegistry.CreateAsync(instanceId, "my-workflow", "Success");

        await _sut.HandleContainerExitAsync(instanceId, "my-workflow", new ContainerExit(0, null));

        var record = await _instanceRegistry.GetAsync(instanceId);
        Assert.That(record!.State, Is.EqualTo("Success"), "a normal exit after completion is not a failure");
    }

    // ------------------------------------------------------------------ Resolution token at rest

    /// <summary>A dispatcher wired like the SetUp one, with the varying collaborators injectable.</summary>
    private WorkflowDispatcher BuildDispatcher(
        WorkflowDispatcherSettings? dispatcherSettings = null,
        Auxilia.PlatformData.Protection.ISettingsProtector? protector = null,
        FakePodHost? podHost = null)
    {
        var settings = dispatcherSettings ?? new WorkflowDispatcherSettings { ContainerExitGraceSeconds = 0 };
        return new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(DefaultSettings()),
            Options.Create(settings),
            CreateHttpClientFactory(_validPackageZip),
            _mockVerifier.Object,
            _mockPendingPackages.Object,
            TestStores.NewSlotProviderRegistry(),
            _tokenRegistry,
            TestStores.NewPolicyEngine(),
            _instanceRegistry,
            TestStores.NewStatusPublisher(_mockBus.Object),
            _schemaStore,
            TestStores.NewWorkflowPackageStore(),
            TestStores.NewArtifactStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(settings),
            TestStores.NewHostPlatformProbe(),
            Mock.Of<IRepositoryAuthResolver>(),
            new AuditLog(_auditRecords, TimeProvider.System),
            TestStores.NewInstanceInfo(),
            protector ?? new Auxilia.PlatformData.Protection.NullSettingsProtector(),
            podHost ?? new FakePodHost(),
            new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            NullLogger<WorkflowDispatcher>.Instance);
    }

    [Test]
    public async Task WhenRunCommandCarriesResolutionToken_PersistedCommandJsonHoldsOnlyTheProtectedForm()
    {
        var protector = new TestStores.PrefixSettingsProtector();
        await _sut.StopAsync();
        _sut = BuildDispatcher(protector: protector);
        await _sut.StartAsync(CancellationToken.None);

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/test.workflow.zip",
            new Dictionary<string, string>(), ResolutionToken: "top-secret-token");

        await _capturedHandler!(command, CancellationToken.None);

        var instanceId = Guid.Parse(captured!.EnvironmentVariables[WorkflowEnvironmentVariables.InstanceId]);
        var record = await _instanceRegistry.GetAsync(instanceId);
        Assert.That(record!.DispatchCommandJson, Is.Not.Null);
        Assert.That(record.DispatchCommandJson, Does.Not.Contain("top-secret-token"),
            "the resolution token is a bearer credential — it must never be persisted in plaintext");

        // The reader path round-trips: deserialize + Unprotect yields the live token.
        var stored = JsonSerializer.Deserialize<RunWorkflowCommand>(record.DispatchCommandJson!)!;
        var unprotected = DispatchCommandProtection.Unprotect(
            stored, protector, NullLogger<WorkflowDispatcherTests>.Instance);
        Assert.That(unprotected.ResolutionToken, Is.EqualTo("top-secret-token"));
    }

    // ------------------------------------------------------------------ Drain crash

    [Test]
    public async Task ContainerExit_WhileDrainingWithNoTerminalMessage_FailsTearsDownAndDispatchesReplacement()
    {
        var protector = new TestStores.PrefixSettingsProtector();
        var podHost = new FakePodHost();
        var dispatcher = BuildDispatcher(protector: protector, podHost: podHost);

        var instanceId = Guid.NewGuid();
        var original = new RunWorkflowCommand(
            Guid.NewGuid(), "service-workflow", "docker://svc",
            new Dictionary<string, string> { ["KEY"] = "value" },
            ResolutionToken: protector.Protect("plain-run-token"));
        await _instanceRegistry.CreateAsync(
            instanceId, "service-workflow", "Draining",
            dispatchCommandJson: JsonSerializer.Serialize(original));

        RunWorkflowCommand? replacement = null;
        _mockBus
            .Setup(b => b.PublishAsync(
                "workflow.run-commands", It.IsAny<RunWorkflowCommand>(), It.IsAny<CancellationToken>()))
            .Callback<string, RunWorkflowCommand, CancellationToken>((_, cmd, _) => replacement = cmd)
            .Returns(Task.CompletedTask);

        await dispatcher.HandleContainerExitAsync(
            instanceId, "service-workflow", new ContainerExit(137, "killed"));

        var record = await _instanceRegistry.GetAsync(instanceId);
        Assert.Multiple(() =>
        {
            Assert.That(record!.State, Is.EqualTo("Failed"),
                "a container crashing mid-drain must fail its run — the terminal message is never coming");
            Assert.That(record.ErrorMessage, Does.Contain("draining").And.Contain("137"));
            Assert.That(podHost.TornDown, Does.Contain(instanceId), "the crashed run's pod must die with it");
        });
        _mockBus.Verify(b => b.PublishToTopicExchangeAsync(
            "workflow.status", It.IsAny<string>(),
            It.Is<WorkflowStatusEvent>(
                e => e.WorkflowInstanceId == instanceId && e.State == "Failed"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(replacement, Is.Not.Null, "the drain-replace contract must survive a crash mid-drain");
        Assert.Multiple(() =>
        {
            Assert.That(replacement!.CommandId, Is.Not.EqualTo(original.CommandId));
            Assert.That(replacement.WorkflowType, Is.EqualTo("service-workflow"));
            Assert.That(replacement.Context, Is.EqualTo(original.Context));
            Assert.That(replacement.ResolutionToken, Is.EqualTo("plain-run-token"),
                "the replacement rides the bus with the live token, not the at-rest ciphertext");
        });
    }

    [Test]
    public async Task ContainerExit_WhileDrainingButTerminalMessageLandsInGrace_ChangesNothing()
    {
        // The graceful-drain race: the record is terminal by the time the drain grace re-read
        // happens (grace = 0 here, so the state set below IS the re-read's view).
        var instanceId = Guid.NewGuid();
        await _instanceRegistry.CreateAsync(instanceId, "service-workflow", "Draining");
        await _instanceRegistry.SetStateAsync(instanceId, "Success");

        await _sut.HandleContainerExitAsync(instanceId, "service-workflow", new ContainerExit(0, null));

        var record = await _instanceRegistry.GetAsync(instanceId);
        Assert.That(record!.State, Is.EqualTo("Success"));
        _mockBus.Verify(b => b.PublishAsync(
                It.IsAny<string>(), It.IsAny<RunWorkflowCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ Crash run-root cleanup

    [Test]
    public async Task ContainerExit_WithoutTerminalState_CleansWorkspaceAndOutputDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"auxilia-dispatcher-crash-{Guid.NewGuid():N}");
        var settings = new WorkflowDispatcherSettings
        {
            ContainerExitGraceSeconds = 0,
            RunOutputDirectory = Path.Combine(tempRoot, "run-output"),
            WorkspaceRootDirectory = Path.Combine(tempRoot, "workspaces")
        };
        var dispatcher = BuildDispatcher(dispatcherSettings: settings);
        try
        {
            var instanceId = Guid.NewGuid();
            await _instanceRegistry.CreateAsync(instanceId, "my-workflow", "Running");
            var workspaceRoot = Path.Combine(settings.WorkspaceRootDirectory, instanceId.ToString("N"));
            var outputRoot = Path.Combine(settings.RunOutputDirectory, instanceId.ToString("N"));
            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(outputRoot);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "partial.json"), "{}");

            await dispatcher.HandleContainerExitAsync(
                instanceId, "my-workflow", new ContainerExit(1, "boom"));

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(workspaceRoot), Is.False,
                    "a crashed run's workspace must die with the run — nothing else will clean it");
                Assert.That(Directory.Exists(outputRoot), Is.False,
                    "a crashed run's output directory must die with the run — nothing else will clean it");
            });
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}

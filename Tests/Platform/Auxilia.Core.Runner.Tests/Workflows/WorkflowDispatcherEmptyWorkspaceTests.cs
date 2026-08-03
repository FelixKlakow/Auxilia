using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Unit tests for the dispatcher's empty-workspace mounts (ARCHITECTURE §9): a mount without
/// a clone-url role materializes as a fresh scratch directory — no git CLI involved.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WorkflowDispatcherEmptyWorkspaceTests
{
    private const string WorkflowType = "empty-workspace-workflow";

    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<IWorkflowLauncher> _mockLauncher = null!;
    private Func<RunWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private List<WorkflowStatusEvent> _statusEvents = null!;
    private WorkflowDispatcher _sut = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;
    private string _tempRoot = null!;
    private WorkflowDispatcherSettings _dispatcherSettings = null!;
    private Mock<IRepositoryAuthResolver> _mockRepoAuth = null!;

    [SetUp]
    public async Task SetUp()
    {
        _mockRepoAuth = new Mock<IRepositoryAuthResolver>();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"auxilia-dispatcher-empty-workspace-{Guid.NewGuid():N}");
        _dispatcherSettings = new WorkflowDispatcherSettings
        {
            WorkspaceRootDirectory = Path.Combine(_tempRoot, "workspaces"),
            WarmCacheDirectory = Path.Combine(_tempRoot, "repo-cache"),
            RunOutputDirectory = Path.Combine(_tempRoot, "run-output")
        };

        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _statusEvents = [];

        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _mockLauncher = new Mock<IWorkflowLauncher>(MockBehavior.Strict);

        var disposable = new Mock<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.DeclareQueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.DeclareTopicExchangeAsync("workflow.status", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishToTopicExchangeAsync(
                "workflow.status", It.IsAny<string>(), It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorkflowStatusEvent, CancellationToken>((_, _, evt, _) => _statusEvents.Add(evt))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.SubscribeAsync<RunWorkflowCommand>(
                It.IsAny<string>(),
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
            Options.Create(new DockerWorkflowLauncherSettings
            {
                RabbitMqHost = "rabbitmq",
                RabbitMqPort = 5672,
                RabbitMqUserName = "guest",
                RabbitMqPassword = "guest"
            }),
            Options.Create(_dispatcherSettings),
            new Mock<IHttpClientFactory>().Object,
            new Mock<IWorkflowPackageVerifier>().Object,
            new Mock<PendingWorkflowPackageStore>().Object,
            TestStores.NewSlotProviderRegistry(),
            new WorkflowInstanceTokenRegistry(
                Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System),
            TestStores.NewPolicyEngine(),
            TestStores.NewWorkflowInstanceRegistry(),
            TestStores.NewStatusPublisher(_mockBus.Object),
            TestStores.NewWorkflowSchemaStore(),
            TestStores.NewWorkflowPackageStore(),
            TestStores.NewArtifactStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(_dispatcherSettings),
            _mockRepoAuth.Object,
            new AuditLog(_auditRecords, TimeProvider.System),
            TestStores.NewInstanceInfo(),
            NullLogger<WorkflowDispatcher>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync();
        _auditRecords.Dispose();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public async Task WhenMountDeclaresNoCloneSource_MaterializesAFreshScratchDirectory_AndAnnouncesTheMountRoot()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        await _capturedHandler!(NewRunCommand(
            new WorkspaceMountDispatch("scratch", "empty-workspace",
                new Dictionary<string, string>())), CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        var instanceId = Guid.Parse(captured!.EnvironmentVariables[WorkflowEnvironmentVariables.InstanceId]);
        Assert.Multiple(() =>
        {
            Assert.That(captured.WorkspaceDirectoryBind, Is.EqualTo(
                Path.Combine(_dispatcherSettings.WorkspaceRootDirectory, instanceId.ToString("N"))));
            Assert.That(captured.EnvironmentVariables[WorkflowEnvironmentVariables.WorkspaceDirectory],
                Is.EqualTo("/workspace"));
            Assert.That(captured.EnvironmentVariables[WorkflowEnvironmentVariables.WorkspaceMountPrefix + "SCRATCH"],
                Is.EqualTo("/workspace/repos/scratch"),
                "The mount root is announced exactly like a git mount's.");
        });
        var mountDir = Path.Combine(captured.WorkspaceDirectoryBind!, "repos", "scratch");
        Assert.That(Directory.Exists(mountDir), Is.True,
            "The empty workspace must be materialized before launch.");
        Assert.That(Directory.EnumerateFileSystemEntries(mountDir), Is.Empty);
        _mockRepoAuth.Verify(r => r.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "An empty workspace resolves no credential.");
    }

    [Test]
    public async Task WhenEmptyMountBindsWorkingDirectoryAndSetupScript_BothRideTheUsualAnnouncements()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        await _capturedHandler!(NewRunCommand(
            new WorkspaceMountDispatch("scratch", "empty-workspace",
                new Dictionary<string, string>
                {
                    ["working-directory"] = "src",
                    ["setup-script"] = "dotnet new console"
                })), CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(captured!.EnvironmentVariables[WorkflowEnvironmentVariables.WorkspaceMountPrefix + "SCRATCH"],
                Is.EqualTo("/workspace/repos/scratch/src"));
            Assert.That(captured.EnvironmentVariables[WorkflowEnvironmentVariables.WorkspaceMountSetupPrefix + "SCRATCH"],
                Is.EqualTo("dotnet new console"),
                "The setup script is announced, never run by the runner.");
        });
        Assert.That(Directory.Exists(Path.Combine(captured!.WorkspaceDirectoryBind!, "repos", "scratch", "src")),
            Is.True, "The bound working directory is pre-created so the announced root exists.");
    }

    [Test]
    public async Task WhenEmptyMountCarriesACredential_RunGoesPreFlightFailed_AndDoesNotLaunch()
    {
        await _capturedHandler!(NewRunCommand(
            new WorkspaceMountDispatch("scratch", "empty-workspace",
                new Dictionary<string, string>(), AuthSlotName: "mount-auth:scratch")), CancellationToken.None);

        _mockLauncher.Verify(
            l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        var failedEvent = _statusEvents.SingleOrDefault(e => e.State == "PreFlightFailed");
        Assert.That(failedEvent, Is.Not.Null,
            "A credentialed mount without a clone source is a misconfiguration, not an empty workspace.");
        Assert.That(failedEvent!.ErrorMessage, Does.Contain("declares no clone source"));
    }

    [Test]
    public async Task WhenEmptyMountIsPrepared_TheWorkspacePreparationIsAudited()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        await _capturedHandler!(NewRunCommand(
            new WorkspaceMountDispatch("scratch", "empty-workspace",
                new Dictionary<string, string>())), CancellationToken.None);

        var instanceId = captured!.EnvironmentVariables[WorkflowEnvironmentVariables.InstanceId];
        var entries = (await _auditRecords.ReadAsync())
            .Where(r => r.Action == "workflow.workspace-prepared").ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Subject, Is.EqualTo(instanceId));
            Assert.That(entries[0].Outcome, Is.EqualTo("1"), "Empty mounts count into the prepared total.");
        });
    }

    // ------------------------------------------------------------------ helpers

    private static RunWorkflowCommand NewRunCommand(params WorkspaceMountDispatch[] mounts)
        => new(Guid.NewGuid(), WorkflowType, "docker://empty-workspace-workflow:test",
            new Dictionary<string, string>(), WorkspaceMounts: mounts);
}

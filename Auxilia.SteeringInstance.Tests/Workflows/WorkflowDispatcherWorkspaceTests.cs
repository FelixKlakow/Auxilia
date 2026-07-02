using System.Diagnostics;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.SteeringInstance.Tests.Workflows;

/// <summary>
/// Component tests for the dispatcher's workspace integration (ARCHITECTURE §9): a real
/// <see cref="WorkspaceManager"/> clones a local-disk origin repo via the git CLI.
/// </summary>
[TestFixture]
[Category("Component")]
public class WorkflowDispatcherWorkspaceTests
{
    private const string WorkflowType = "workspace-workflow";

    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<IWorkflowLauncher> _mockLauncher = null!;
    private Func<RunWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private List<WorkflowStatusEvent> _statusEvents = null!;
    private WorkflowDispatcher _sut = null!;
    private WorkflowSchemaStore _schemaStore = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;
    private string _tempRoot = null!;
    private string _originRepo = null!;
    private WorkflowDispatcherSettings _dispatcherSettings = null!;

    [SetUp]
    public async Task SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"auxilia-dispatcher-workspace-{Guid.NewGuid():N}");
        _originRepo = Path.Combine(_tempRoot, "origin");
        CreateOriginRepo(_originRepo);

        _dispatcherSettings = new WorkflowDispatcherSettings
        {
            WorkspaceRootDirectory = Path.Combine(_tempRoot, "workspaces"),
            WarmCacheDirectory = Path.Combine(_tempRoot, "repo-cache"),
            RunOutputDirectory = Path.Combine(_tempRoot, "run-output")
        };

        _schemaStore = TestStores.NewWorkflowSchemaStore();
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
            .Setup(b => b.DeclareExchangeAsync("workflow.status-events", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishToExchangeAsync(
                "workflow.status-events", It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorkflowStatusEvent, CancellationToken>((_, evt, _) => _statusEvents.Add(evt))
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
            .Returns(Task.CompletedTask);

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
            TestStores.NewSlotConfigurationStore(),
            TestStores.NewSlotProviderRegistry(),
            TestStores.NewWorkflowConfigurationStore(),
            new WorkflowInstanceTokenRegistry(
                Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System),
            TestStores.NewPolicyEngine(),
            TestStores.NewWorkflowInstanceRegistry(),
            TestStores.NewStatusPublisher(_mockBus.Object),
            _schemaStore,
            TestStores.NewWorkflowPackageStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(_dispatcherSettings),
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
            DeleteDirectory(_tempRoot);
    }

    [Test]
    public async Task WhenSchemaDeclaresRepository_LaunchRequestCarriesWorkspaceBindAndEnvVar()
    {
        await SeedSchemaAsync(new RepositoryDeclaration("main", _originRepo));

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        await _capturedHandler!(NewRunCommand(), CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        var instanceId = Guid.Parse(captured!.EnvironmentVariables[WorkflowEnvironmentVariables.InstanceId]);
        Assert.Multiple(() =>
        {
            Assert.That(captured.WorkspaceDirectoryBind, Is.EqualTo(
                Path.Combine(_dispatcherSettings.WorkspaceRootDirectory, instanceId.ToString("N"))));
            Assert.That(captured.EnvironmentVariables[WorkflowEnvironmentVariables.WorkspaceDirectory],
                Is.EqualTo("/workspace"));
        });
        Assert.That(File.Exists(Path.Combine(captured.WorkspaceDirectoryBind!, "repos", "main", "test.txt")),
            Is.True, "The prepared workspace must contain the cloned repository.");
    }

    [Test]
    public async Task WhenSchemaDeclaresRepository_WorkspacePreparationIsAudited()
    {
        await SeedSchemaAsync(new RepositoryDeclaration("main", _originRepo));

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        await _capturedHandler!(NewRunCommand(), CancellationToken.None);

        var instanceId = captured!.EnvironmentVariables[WorkflowEnvironmentVariables.InstanceId];
        var entries = (await _auditRecords.ReadAsync())
            .Where(r => r.Action == "workflow.workspace-prepared").ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Subject, Is.EqualTo(instanceId));
            Assert.That(entries[0].Outcome, Is.EqualTo("1"));
        });
    }

    [Test]
    public async Task WhenSchemaDeclaresNoRepository_NoWorkspaceBindAndNoEnvVar()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        await _capturedHandler!(NewRunCommand(), CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(captured!.WorkspaceDirectoryBind, Is.Null);
            Assert.That(captured.EnvironmentVariables.ContainsKey(
                WorkflowEnvironmentVariables.WorkspaceDirectory), Is.False);
        });
    }

    [Test]
    public async Task WhenPreparationFails_RunGoesPreFlightFailed_AndDoesNotLaunch()
    {
        await SeedSchemaAsync(new RepositoryDeclaration("main", Path.Combine(_tempRoot, "does-not-exist")));

        await _capturedHandler!(NewRunCommand(), CancellationToken.None);

        _mockLauncher.Verify(
            l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var failedEvent = _statusEvents.SingleOrDefault(e => e.State == "PreFlightFailed");
        Assert.That(failedEvent, Is.Not.Null, "A PreFlightFailed status event must be published.");
        Assert.That(failedEvent!.ErrorMessage, Does.Contain("workspace preparation failed"));
    }

    // ------------------------------------------------------------------ helpers

    private Task SeedSchemaAsync(params RepositoryDeclaration[] repositories)
        => _schemaStore.SetSchemaAsync(WorkflowType,
            new WorkflowSchema(WorkflowType, [], []) { Repositories = repositories });

    private static RunWorkflowCommand NewRunCommand()
        => new(Guid.NewGuid(), WorkflowType, "docker://workspace-workflow:test",
            new Dictionary<string, string>());

    private static void CreateOriginRepo(string path)
    {
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "config", "user.email", "component-test@auxilia.local");
        RunGit(path, "config", "user.name", "Auxilia Component Test");
        File.WriteAllText(Path.Combine(path, "test.txt"), "hello from the dispatcher workspace test");
        RunGit(path, "add", "test.txt");
        RunGit(path, "commit", "-m", "test: add test.txt");
    }

    private static void RunGit(string workingDirectory, params string[] gitArgs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in gitArgs)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start git process.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {string.Join(' ', gitArgs)} failed (exit {process.ExitCode}): {stderr}");
        }
    }

    /// <summary>Deletes recursively, clearing the read-only attributes git sets on object files.</summary>
    private static void DeleteDirectory(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}

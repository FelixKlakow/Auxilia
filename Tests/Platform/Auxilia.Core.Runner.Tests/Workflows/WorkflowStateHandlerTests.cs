using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData.Artifacts;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowStateHandlerTests
{
    private const string CommandQueue = "test-run-commands";

    private Mock<IMessageBusClient> _mockBus = null!;
    private Func<WorkflowStateMessage, CancellationToken, Task>? _capturedHandler;
    private WorkflowInstanceRegistry _registry = null!;
    private WorkflowInstanceTokenRegistry _tokenRegistry = null!;
    private CoreRunnerInfo _runnerInfo = null!;
    private string _tempRoot = null!;
    private WorkflowDispatcherSettings _settings = null!;
    private IArtifactStore _artifactStore = null!;
    private WorkflowStateHandler _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);

        var disposable = new Mock<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.DeclareExchangeAsync("workflow.state", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // The handler republishes every state change as a WorkflowStatusEvent.
        _mockBus
            .Setup(b => b.DeclareTopicExchangeAsync("workflow.status", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishToTopicExchangeAsync(
                "workflow.status", It.IsAny<string>(), It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockBus
            .Setup(b => b.SubscribeToExchangeAsync<WorkflowStateMessage>(
                "workflow.state",
                It.IsAny<Func<WorkflowStateMessage, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<WorkflowStateMessage, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(disposable.Object);

        _registry = TestStores.NewWorkflowInstanceRegistry();
        _tokenRegistry = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System);
        _runnerInfo = TestStores.NewInstanceInfo();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"auxilia-state-handler-{Guid.NewGuid():N}");
        _settings = new WorkflowDispatcherSettings
        {
            CommandQueueName = CommandQueue,
            RunOutputDirectory = Path.Combine(_tempRoot, "run-output"),
            WorkspaceRootDirectory = Path.Combine(_tempRoot, "workspaces")
        };
        _artifactStore = TestStores.NewArtifactStore(Path.Combine(_tempRoot, "platform-data"));
        _sut = new WorkflowStateHandler(
            _mockBus.Object,
            _registry,
            _tokenRegistry,
            _runnerInfo,
            TestStores.NewAuditLog(),
            TestStores.NewStatusPublisher(_mockBus.Object),
            TestStores.NewArtifactPersister(_mockBus.Object, _artifactStore, _settings),
            TestStores.NewWorkspaceManager(_settings),
            new FakePodHost(),
            new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            Options.Create(_settings),
            new Auxilia.PlatformData.Protection.NullSettingsProtector(),
            NullLogger<WorkflowStateHandler>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    /// <summary>A run this runner launched: token issued for the type, record owned by us.</summary>
    private async Task<IssuedInstanceToken> OwnedRunAsync(
        string workflowType, string state = "Running", string? dispatchCommandJson = null,
        Guid? ownerServiceId = null)
    {
        var issued = _tokenRegistry.Issue(workflowType);
        await _registry.CreateAsync(
            issued.WorkflowInstanceId, workflowType, state,
            ownerServiceId ?? _runnerInfo.ServiceId, dispatchCommandJson);
        return issued;
    }

    private static WorkflowStateMessage Terminal(
        IssuedInstanceToken run, string workflowType, WorkflowState state = WorkflowState.Success,
        string? error = null)
        => new(run.WorkflowInstanceId, state, error, workflowType, run.Token);

    private void VerifyNoStatusPublished()
        => _mockBus.Verify(
            b => b.PublishToTopicExchangeAsync(
                "workflow.status", It.IsAny<string>(), It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public void WhenStartCalled_DeclaresWorkflowStateQueue()
    {
        _mockBus.Verify(
            b => b.DeclareExchangeAsync("workflow.state", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void WhenStartCalled_SubscribesToWorkflowStateQueue()
    {
        _mockBus.Verify(
            b => b.SubscribeToExchangeAsync<WorkflowStateMessage>(
                "workflow.state",
                It.IsAny<Func<WorkflowStateMessage, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void WhenSuccessStateReceived_DoesNotThrow()
    {
        var message = new WorkflowStateMessage(Guid.NewGuid(), WorkflowState.Success, null);

        Assert.DoesNotThrowAsync(() => _capturedHandler!(message, CancellationToken.None));
    }

    [Test]
    public void WhenFailedStateReceived_DoesNotThrow()
    {
        var message = new WorkflowStateMessage(Guid.NewGuid(), WorkflowState.Failed, "something went wrong");

        Assert.DoesNotThrowAsync(() => _capturedHandler!(message, CancellationToken.None));
    }

    [Test]
    public void WhenCancelledStateReceived_DoesNotThrow()
    {
        var message = new WorkflowStateMessage(Guid.NewGuid(), WorkflowState.Cancelled, null);

        Assert.DoesNotThrowAsync(() => _capturedHandler!(message, CancellationToken.None));
    }

    // ------------------------------------------------------------------ Instance token consumption

    [Test]
    public async Task WhenStateReceived_ConsumesInstanceToken()
    {
        var issued = await OwnedRunAsync("wf");
        Assert.That(_tokenRegistry.Validate(issued.WorkflowInstanceId, issued.Token), Is.True);

        await _capturedHandler!(Terminal(issued, "wf"), CancellationToken.None);

        Assert.That(_tokenRegistry.Validate(issued.WorkflowInstanceId, issued.Token), Is.False,
            "The instance credential must die with the run.");
    }

    // ------------------------------------------------------------------ Authentication + ownership

    [Test]
    public async Task WhenTokenMissing_IgnoresTheMessage()
    {
        var issued = await OwnedRunAsync("wf");
        var runRoot = Path.Combine(_settings.WorkspaceRootDirectory, issued.WorkflowInstanceId.ToString("N"));
        Directory.CreateDirectory(runRoot);

        await _capturedHandler!(
            new WorkflowStateMessage(issued.WorkflowInstanceId, WorkflowState.Success, null, "wf", InstanceToken: null),
            CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(_tokenRegistry.Validate(issued.WorkflowInstanceId, issued.Token), Is.True,
                "An unauthenticated report must not consume the credential.");
            Assert.That(Directory.Exists(runRoot), Is.True, "An unauthenticated report must not clean up.");
            Assert.That((await _registry.GetAsync(issued.WorkflowInstanceId))!.State, Is.EqualTo("Running"));
        });
        VerifyNoStatusPublished();
    }

    [Test]
    public async Task WhenAnotherRunsTokenIsPresented_IgnoresTheMessage()
    {
        // Every container holds the bus password: run B tries to end run A with B's own token.
        var victim = await OwnedRunAsync("wf");
        var attacker = await OwnedRunAsync("wf");
        var runRoot = Path.Combine(_settings.WorkspaceRootDirectory, victim.WorkflowInstanceId.ToString("N"));
        Directory.CreateDirectory(runRoot);

        await _capturedHandler!(
            new WorkflowStateMessage(victim.WorkflowInstanceId, WorkflowState.Success, null, "wf", attacker.Token),
            CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(_tokenRegistry.Validate(victim.WorkflowInstanceId, victim.Token), Is.True);
            Assert.That(Directory.Exists(runRoot), Is.True);
            Assert.That((await _registry.GetAsync(victim.WorkflowInstanceId))!.State, Is.EqualTo("Running"));
        });
        VerifyNoStatusPublished();
    }

    [Test]
    public async Task WhenWorkflowNameDoesNotMatchTheIssuedType_IgnoresTheMessage()
    {
        var issued = await OwnedRunAsync("wf-a");

        await _capturedHandler!(Terminal(issued, "wf-b"), CancellationToken.None);

        Assert.That(_tokenRegistry.Validate(issued.WorkflowInstanceId, issued.Token), Is.True,
            "A report under another type must be rejected, not consumed.");
        VerifyNoStatusPublished();
    }

    [Test]
    public async Task WhenNoRecordExists_PublishesNothing()
    {
        // A valid token without a lifecycle record is not ours to report on.
        var issued = _tokenRegistry.Issue("wf");

        await _capturedHandler!(Terminal(issued, "wf"), CancellationToken.None);

        VerifyNoStatusPublished();
    }

    [Test]
    public async Task WhenRecordIsOwnedByAnotherRunner_IgnoresTheMessage()
    {
        // The state exchange fans out to every runner: a non-owner must neither publish a
        // status (it would corrupt the run's type to "unknown") nor tear anything down.
        var issued = await OwnedRunAsync("wf", ownerServiceId: Guid.NewGuid());
        var runRoot = Path.Combine(_settings.WorkspaceRootDirectory, issued.WorkflowInstanceId.ToString("N"));
        Directory.CreateDirectory(runRoot);

        await _capturedHandler!(Terminal(issued, "wf"), CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(_tokenRegistry.Validate(issued.WorkflowInstanceId, issued.Token), Is.True);
            Assert.That(Directory.Exists(runRoot), Is.True);
            Assert.That((await _registry.GetAsync(issued.WorkflowInstanceId))!.State, Is.EqualTo("Running"));
        });
        VerifyNoStatusPublished();
    }

    [Test]
    public async Task WhenOwnedAndAuthenticated_PublishesTerminalStatusWithTheRecordedType()
    {
        var issued = await OwnedRunAsync("wf");
        WorkflowStatusEvent? published = null;
        _mockBus
            .Setup(b => b.PublishToTopicExchangeAsync(
                "workflow.status", It.IsAny<string>(), It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorkflowStatusEvent, CancellationToken>((_, _, e, _) => published = e)
            .Returns(Task.CompletedTask);

        await _capturedHandler!(Terminal(issued, "wf", WorkflowState.Failed, "boom"), CancellationToken.None);

        Assert.That(published, Is.Not.Null);
        Assert.That(published!.WorkflowType, Is.EqualTo("wf"));
        Assert.That(published.State, Is.EqualTo("Failed"));
        Assert.That((await _registry.GetAsync(issued.WorkflowInstanceId))!.State, Is.EqualTo("Failed"));
    }

    // ------------------------------------------------------------------ Workspace cleanup

    [TestCase(WorkflowState.Success)]
    [TestCase(WorkflowState.Failed)]
    [TestCase(WorkflowState.Cancelled)]
    public async Task WhenTerminalStateReceived_RemovesRunWorkspace(WorkflowState state)
    {
        var issued = await OwnedRunAsync("wf");
        var runRoot = Path.Combine(_settings.WorkspaceRootDirectory, issued.WorkflowInstanceId.ToString("N"));
        Directory.CreateDirectory(runRoot);

        await _capturedHandler!(Terminal(issued, "wf", state), CancellationToken.None);

        Assert.That(Directory.Exists(runRoot), Is.False,
            "The run's workspace must die with the run.");
    }

    [TestCase(WorkflowState.Success)]
    [TestCase(WorkflowState.Failed)]
    [TestCase(WorkflowState.Cancelled)]
    public async Task WhenTerminalStateReceived_RemovesTheRunOutputDirectory_EvenWithoutDeclaredOutputs(
        WorkflowState state)
    {
        var issued = await OwnedRunAsync("wf");
        var outputRoot = Path.Combine(_settings.RunOutputDirectory, issued.WorkflowInstanceId.ToString("N"));
        Directory.CreateDirectory(outputRoot);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "scratch.txt"), "left behind by the run");

        await _capturedHandler!(Terminal(issued, "wf", state), CancellationToken.None);

        Assert.That(Directory.Exists(outputRoot), Is.False,
            "The per-run output directory must be swept on every terminal path, not only Success-with-outputs.");
    }

    // ------------------------------------------------------------------ Drain-and-replace

    [Test]
    public async Task WhenDrainingInstanceReachesTerminalState_RepublishesStoredCommandWithFreshCommandId()
    {
        var original = new RunWorkflowCommand(
            Guid.NewGuid(), "service-workflow", "https://example.com/service-workflow.zip",
            new Dictionary<string, string> { ["KEY"] = "value" });
        var issued = await OwnedRunAsync(
            "service-workflow", "Draining", JsonSerializer.Serialize(original));

        RunWorkflowCommand? replacement = null;
        _mockBus
            .Setup(b => b.PublishAsync(CommandQueue, It.IsAny<RunWorkflowCommand>(), It.IsAny<CancellationToken>()))
            .Callback<string, RunWorkflowCommand, CancellationToken>((_, cmd, _) => replacement = cmd)
            .Returns(Task.CompletedTask);

        await _capturedHandler!(Terminal(issued, "service-workflow"), CancellationToken.None);

        _mockBus.Verify(
            b => b.PublishAsync(CommandQueue, It.IsAny<RunWorkflowCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(replacement, Is.Not.Null);
        Assert.That(replacement!.CommandId, Is.Not.EqualTo(original.CommandId), "Replacement must use a fresh CommandId.");
        Assert.That(replacement.WorkflowType, Is.EqualTo(original.WorkflowType));
        Assert.That(replacement.WorkflowPackageUri, Is.EqualTo(original.WorkflowPackageUri));
        Assert.That(replacement.Context, Is.EqualTo(original.Context));
    }

    [Test]
    public async Task WhenDrainingInstanceHasProtectedResolutionToken_ReplacementCarriesThePlaintext()
    {
        // The stored dispatch command protects its resolution token at rest; the replacement
        // must ride the bus with the plaintext, exactly like the original dispatch did.
        var protector = new TestStores.PrefixSettingsProtector();
        var handler = new WorkflowStateHandler(
            _mockBus.Object, _registry, _tokenRegistry, _runnerInfo, TestStores.NewAuditLog(),
            TestStores.NewStatusPublisher(_mockBus.Object),
            TestStores.NewArtifactPersister(_mockBus.Object, _artifactStore, _settings),
            TestStores.NewWorkspaceManager(_settings),
            new FakePodHost(), new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            Options.Create(_settings), protector, NullLogger<WorkflowStateHandler>.Instance);
        await handler.StartAsync(CancellationToken.None);

        var stored = new RunWorkflowCommand(
            Guid.NewGuid(), "service-workflow", "docker://svc",
            new Dictionary<string, string>(),
            ResolutionToken: protector.Protect("plain-resolution-token"));
        var issued = await OwnedRunAsync(
            "service-workflow", "Draining", JsonSerializer.Serialize(stored));

        RunWorkflowCommand? replacement = null;
        _mockBus
            .Setup(b => b.PublishAsync(CommandQueue, It.IsAny<RunWorkflowCommand>(), It.IsAny<CancellationToken>()))
            .Callback<string, RunWorkflowCommand, CancellationToken>((_, cmd, _) => replacement = cmd)
            .Returns(Task.CompletedTask);

        await _capturedHandler!(Terminal(issued, "service-workflow"), CancellationToken.None);
        await handler.StopAsync();

        Assert.That(replacement, Is.Not.Null);
        Assert.That(replacement!.ResolutionToken, Is.EqualTo("plain-resolution-token"),
            "The replacement dispatch must carry the live token, not the at-rest ciphertext.");
    }

    [Test]
    public async Task WhenRunningInstanceReachesTerminalState_DoesNotRedispatch()
    {
        var original = new RunWorkflowCommand(
            Guid.NewGuid(), "service-workflow", "https://example.com/service-workflow.zip",
            new Dictionary<string, string>());
        var issued = await OwnedRunAsync(
            "service-workflow", "Running", JsonSerializer.Serialize(original));

        await _capturedHandler!(Terminal(issued, "service-workflow"), CancellationToken.None);

        _mockBus.Verify(
            b => b.PublishAsync(CommandQueue, It.IsAny<RunWorkflowCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ Artifact persistence

    [Test]
    public async Task WhenSuccessWithDeclaredOutputs_PersistsArtifactWithWorkItemIdFromDispatchContext()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "review-workflow", "docker://review-workflow:test",
            new Dictionary<string, string> { ["WorkItemId"] = "WI-1" });
        var issued = await OwnedRunAsync(
            "review-workflow", "Running", JsonSerializer.Serialize(command));
        var instanceId = issued.WorkflowInstanceId;
        await _registry.RegisterAsync(
            instanceId, "review-workflow",
            outputsJson: JsonSerializer.Serialize(new List<WorkflowOutputDescriptor>
            {
                new("review-result", "result.json", null)
            }));

        var outputDir = Path.Combine(_settings.RunOutputDirectory, instanceId.ToString("N"));
        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "result.json"), """{"verdict":"approve"}""");

        _mockBus
            .Setup(b => b.DeclareTopicExchangeAsync(
                ArtifactPersistedEvent.ExchangeName, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishToTopicExchangeAsync(
                ArtifactPersistedEvent.ExchangeName, It.IsAny<string>(), It.IsAny<ArtifactPersistedEvent>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _capturedHandler!(Terminal(issued, "review-workflow"), CancellationToken.None);

        var lineage = await _artifactStore.GetLineageAsync("review-result", "WI-1");
        Assert.That(lineage, Has.Count.EqualTo(1), "The declared output must be persisted for the dispatch's work item.");
        Assert.That(lineage[0].WorkItemId, Is.EqualTo("WI-1"));
        Assert.That(lineage[0].RunInstanceId, Is.EqualTo(instanceId));
        Assert.That(lineage[0].WorkflowType, Is.EqualTo("review-workflow"));
    }
}

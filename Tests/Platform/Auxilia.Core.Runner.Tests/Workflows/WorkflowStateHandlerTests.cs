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
            TestStores.NewAuditLog(),
            TestStores.NewStatusPublisher(_mockBus.Object),
            TestStores.NewArtifactPersister(_mockBus.Object, _artifactStore, _settings),
            TestStores.NewWorkspaceManager(_settings),
            new FakePodHost(),
            new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            Options.Create(_settings),
            NullLogger<WorkflowStateHandler>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

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
        var issued = _tokenRegistry.Issue("wf");
        Assert.That(_tokenRegistry.Validate(issued.WorkflowInstanceId, issued.Token), Is.True);

        await _capturedHandler!(
            new WorkflowStateMessage(issued.WorkflowInstanceId, WorkflowState.Success, null),
            CancellationToken.None);

        Assert.That(_tokenRegistry.Validate(issued.WorkflowInstanceId, issued.Token), Is.False,
            "The instance credential must die with the run.");
    }

    // ------------------------------------------------------------------ Workspace cleanup

    [TestCase(WorkflowState.Success)]
    [TestCase(WorkflowState.Failed)]
    [TestCase(WorkflowState.Cancelled)]
    public async Task WhenTerminalStateReceived_RemovesRunWorkspace(WorkflowState state)
    {
        var instanceId = Guid.NewGuid();
        var runRoot = Path.Combine(_settings.WorkspaceRootDirectory, instanceId.ToString("N"));
        Directory.CreateDirectory(runRoot);

        await _capturedHandler!(
            new WorkflowStateMessage(instanceId, state, null), CancellationToken.None);

        Assert.That(Directory.Exists(runRoot), Is.False,
            "The run's workspace must die with the run.");
    }

    // ------------------------------------------------------------------ Drain-and-replace

    [Test]
    public async Task WhenDrainingInstanceReachesTerminalState_RepublishesStoredCommandWithFreshCommandId()
    {
        var instanceId = Guid.NewGuid();
        var original = new RunWorkflowCommand(
            Guid.NewGuid(), "service-workflow", "https://example.com/service-workflow.zip",
            new Dictionary<string, string> { ["KEY"] = "value" });
        await _registry.CreateAsync(
            instanceId, "service-workflow", "Draining",
            dispatchCommandJson: JsonSerializer.Serialize(original));

        RunWorkflowCommand? replacement = null;
        _mockBus
            .Setup(b => b.PublishAsync(CommandQueue, It.IsAny<RunWorkflowCommand>(), It.IsAny<CancellationToken>()))
            .Callback<string, RunWorkflowCommand, CancellationToken>((_, cmd, _) => replacement = cmd)
            .Returns(Task.CompletedTask);

        await _capturedHandler!(
            new WorkflowStateMessage(instanceId, WorkflowState.Success, null), CancellationToken.None);

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
    public async Task WhenRunningInstanceReachesTerminalState_DoesNotRedispatch()
    {
        var instanceId = Guid.NewGuid();
        var original = new RunWorkflowCommand(
            Guid.NewGuid(), "service-workflow", "https://example.com/service-workflow.zip",
            new Dictionary<string, string>());
        await _registry.CreateAsync(
            instanceId, "service-workflow", "Running",
            dispatchCommandJson: JsonSerializer.Serialize(original));

        await _capturedHandler!(
            new WorkflowStateMessage(instanceId, WorkflowState.Success, null), CancellationToken.None);

        _mockBus.Verify(
            b => b.PublishAsync(CommandQueue, It.IsAny<RunWorkflowCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ Artifact persistence

    [Test]
    public async Task WhenSuccessWithDeclaredOutputs_PersistsArtifactWithWorkItemIdFromDispatchContext()
    {
        var instanceId = Guid.NewGuid();
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "review-workflow", "docker://review-workflow:test",
            new Dictionary<string, string> { ["WorkItemId"] = "WI-1" });
        await _registry.CreateAsync(
            instanceId, "review-workflow", "Running",
            dispatchCommandJson: JsonSerializer.Serialize(command));
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

        await _capturedHandler!(
            new WorkflowStateMessage(instanceId, WorkflowState.Success, null), CancellationToken.None);

        var lineage = await _artifactStore.GetLineageAsync("review-result", "WI-1");
        Assert.That(lineage, Has.Count.EqualTo(1), "The declared output must be persisted for the dispatch's work item.");
        Assert.That(lineage[0].WorkItemId, Is.EqualTo("WI-1"));
        Assert.That(lineage[0].RunInstanceId, Is.EqualTo(instanceId));
        Assert.That(lineage[0].WorkflowType, Is.EqualTo("review-workflow"));
    }
}

using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowStateHandlerTests
{
    private const string CommandQueue = "test-run-commands";

    private Mock<IMessageBusClient> _mockBus = null!;
    private Func<WorkflowStateMessage, CancellationToken, Task>? _capturedHandler;
    private WorkflowInstanceRegistry _registry = null!;
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
            .Setup(b => b.DeclareExchangeAsync("workflow.status-events", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishToExchangeAsync(
                "workflow.status-events", It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
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
        _sut = new WorkflowStateHandler(
            _mockBus.Object,
            _registry,
            TestStores.NewAuditLog(),
            TestStores.NewStatusPublisher(_mockBus.Object),
            Options.Create(new WorkflowDispatcherSettings { CommandQueueName = CommandQueue }),
            NullLogger<WorkflowStateHandler>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown() => await _sut.StopAsync();

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
}

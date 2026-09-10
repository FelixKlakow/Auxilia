using Auxilia.Messaging;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowCancelDispatcherTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Func<CancelWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private WorkflowCancelDispatcher _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);

        var disposable = new Mock<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.DeclareQueueAsync("workflow.cancel-commands", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockBus
            .Setup(b => b.SubscribeAsync<CancelWorkflowCommand>(
                "workflow.cancel-commands",
                It.IsAny<Func<CancelWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<CancelWorkflowCommand, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(disposable.Object);

        _sut = new WorkflowCancelDispatcher(
            _mockBus.Object,
            NullLogger<WorkflowCancelDispatcher>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown() => await _sut.StopAsync();

    [Test]
    public void WhenStartCalled_DeclaresWorkflowCancelCommandsQueue()
    {
        _mockBus.Verify(
            b => b.DeclareQueueAsync("workflow.cancel-commands", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void WhenStartCalled_SubscribesToWorkflowCancelCommandsQueue()
    {
        _mockBus.Verify(
            b => b.SubscribeAsync<CancelWorkflowCommand>(
                "workflow.cancel-commands",
                It.IsAny<Func<CancelWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenCancelReceived_PublishesCancelCommandToPerInstanceTopic()
    {
        var instanceId = Guid.NewGuid();

        var command = new CancelWorkflowCommand(instanceId);
        var expectedTopic = $"workflow-cancel-{instanceId}";

        // Declare-before-publish: a cancel racing the workflow's startup must park on the
        // queue instead of being dropped unrouted.
        _mockBus
            .Setup(b => b.DeclareQueueAsync(expectedTopic, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishAsync(
                expectedTopic,
                command,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _capturedHandler!(command, CancellationToken.None);

        _mockBus.Verify(
            b => b.DeclareQueueAsync(expectedTopic, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockBus.Verify(
            b => b.PublishAsync(
                expectedTopic,
                command,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenInstanceIsNotOwnedByThisRunner_StillForwardsToThePerInstanceQueue()
    {
        // The shared cancel queue hands the command to ONE runner of the pool — not necessarily
        // the owner. Forwarding by id is what makes the per-instance queue the meeting point.
        var instanceId = Guid.NewGuid();
        var command = new CancelWorkflowCommand(instanceId);
        var expectedTopic = $"workflow-cancel-{instanceId}";
        _mockBus
            .Setup(b => b.DeclareQueueAsync(expectedTopic, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishAsync(expectedTopic, command, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _capturedHandler!(command, CancellationToken.None);

        _mockBus.Verify(
            b => b.PublishAsync(expectedTopic, command, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

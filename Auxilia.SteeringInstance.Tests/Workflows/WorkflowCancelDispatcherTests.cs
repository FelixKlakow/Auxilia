using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowCancelDispatcherTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Func<CancelWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private WorkflowInstanceRegistry _registry = null!;
    private WorkflowCancelDispatcher _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _registry = TestStores.NewWorkflowInstanceRegistry();

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
            _registry,
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
    public async Task WhenKnownInstanceReceived_PublishesCancelCommandToPerInstanceTopic()
    {
        var instanceId = Guid.NewGuid();
        await _registry.RegisterAsync(instanceId, "test-workflow");

        var command = new CancelWorkflowCommand(instanceId);
        var expectedTopic = $"workflow-cancel-{instanceId}";

        _mockBus
            .Setup(b => b.PublishAsync(
                expectedTopic,
                command,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _capturedHandler!(command, CancellationToken.None);

        _mockBus.Verify(
            b => b.PublishAsync(
                expectedTopic,
                command,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenUnknownInstanceReceived_DoesNotPublishAnything()
    {
        var command = new CancelWorkflowCommand(Guid.NewGuid());

        // Act + Assert: strict mock has no PublishAsync setup, so any call would throw MockException.
        // Completing without exception proves PublishAsync was never invoked.
        Assert.DoesNotThrowAsync(() => _capturedHandler!(command, CancellationToken.None));
    }
}

using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowStateHandlerTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Func<WorkflowStateMessage, CancellationToken, Task>? _capturedHandler;
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

        _mockBus
            .Setup(b => b.SubscribeToExchangeAsync<WorkflowStateMessage>(
                "workflow.state",
                It.IsAny<Func<WorkflowStateMessage, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<WorkflowStateMessage, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(disposable.Object);

        _sut = new WorkflowStateHandler(
            _mockBus.Object,
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
}

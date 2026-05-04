using Auxilia.Messaging;
using Auxilia.Messaging.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class IdentificationRequestHandlerTests
{
    private static IConfiguration EmptyConfig => new ConfigurationBuilder().Build();

    [SetUp]
    public async Task SetUp()
    {
        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _serviceInfo = new ServiceInfo();

        // Capture the subscription handler so we can invoke it directly
        var subscriptionHandle = new Mock<IAsyncDisposable>();
        subscriptionHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.SubscribeAsync<IdentificationRequestMessage>(
                QueueInitializer.DefaultQueueName,
                It.IsAny<Func<IdentificationRequestMessage, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<IdentificationRequestMessage, CancellationToken, Task>, CancellationToken>((_,
                handler, _) => _capturedHandler = handler)
            .ReturnsAsync(subscriptionHandle.Object);

        _mockBus
            .Setup(b => b.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<IdentificationResponseMessage>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new IdentificationRequestHandler(
            _mockBus.Object,
            _serviceInfo,
            EmptyConfig,
            NullLogger<IdentificationRequestHandler>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    // ...existing code...

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
    }

    private Mock<IMessageBusClient> _mockBus = null!;
    private ServiceInfo _serviceInfo = null!;
    private IdentificationRequestHandler _sut = null!;
    private Func<IdentificationRequestMessage, CancellationToken, Task>? _capturedHandler;

    [Test]
    public async Task WhenIdentificationRequestReceived_PublishesResponseToResponseTopic()
    {
        // Arrange
        var responseTopic = "steering-instance-responses";
        var request = new IdentificationRequestMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            responseTopic);

        // Act
        await _capturedHandler!(request, CancellationToken.None);

        // Assert
        _mockBus.Verify(
            b => b.PublishAsync(
                responseTopic,
                It.IsAny<IdentificationResponseMessage>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenIdentificationRequestReceived_ResponseContainsCorrectServicePurpose()
    {
        // Arrange
        IdentificationResponseMessage? captured = null;
        _mockBus
            .Setup(b => b.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<IdentificationResponseMessage>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IdentificationResponseMessage, CancellationToken>((_, msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var request = new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), "some-topic");

        // Act
        await _capturedHandler!(request, CancellationToken.None);

        // Assert
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.ServicePurpose, Is.EqualTo("AuxiliaBackendService"));
        Assert.That(captured.ServiceId, Is.EqualTo(_serviceInfo.ServiceId));
        Assert.That(captured.StartupTimeUtc, Is.EqualTo(_serviceInfo.StartupTimeUtc));
    }
}
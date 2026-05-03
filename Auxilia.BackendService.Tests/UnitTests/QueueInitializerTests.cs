using Auxilia.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class QueueInitializerTests
{
    private static IConfiguration EmptyConfig => new ConfigurationBuilder().Build();

    [Test]
    public async Task StartAsync_DeclaresBackendServiceQueue_ExactlyOnce()
    {
        // Arrange
        var mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        mockBus
            .Setup(b => b.DeclareQueueAsync(QueueInitializer.DefaultQueueName, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new QueueInitializer(mockBus.Object, EmptyConfig, NullLogger<QueueInitializer>.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        mockBus.Verify(
            b => b.DeclareQueueAsync(QueueInitializer.DefaultQueueName, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task StartAsync_WhenQueueNameConfigured_DeclaresThatQueue()
    {
        // Arrange
        const string customQueue = "my-custom-queue";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BackendService:QueueName"] = customQueue })
            .Build();

        var mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        mockBus
            .Setup(b => b.DeclareQueueAsync(customQueue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new QueueInitializer(mockBus.Object, config, NullLogger<QueueInitializer>.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        mockBus.Verify(b => b.DeclareQueueAsync(customQueue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task StopAsync_DoesNothing()
    {
        var mockBus = new Mock<IMessageBusClient>();
        var sut = new QueueInitializer(mockBus.Object, EmptyConfig, NullLogger<QueueInitializer>.Instance);
        await sut.StopAsync(CancellationToken.None); // must not throw
    }
}
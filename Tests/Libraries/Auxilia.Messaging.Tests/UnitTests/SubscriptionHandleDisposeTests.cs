using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;

namespace Auxilia.Messaging.Tests.UnitTests;

/// <summary>
/// The subscription handles against mocked channels: disposing must release EVERY channel even
/// when the broker-side consumer cancel throws (the channel is already closed after a
/// connection recovery). Publisher confirmations themselves need a real broker — the Docker
/// system suite (<c>Tests/System/Auxilia.SystemTestSuite/Messaging</c>) covers the publish path.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class SubscriptionHandleDisposeTests
{
    private static Mock<IChannel> ClosedConsumerChannel()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicCancelAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel already closed"));
        channel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask).Verifiable();
        return channel;
    }

    [Test]
    public async Task SubscriptionHandle_DisposesTheChannel_WhenTheConsumerCancelThrows()
    {
        var channel = ClosedConsumerChannel();
        var handle = new RabbitMqClient.SubscriptionHandle(channel.Object, "tag", NullLogger.Instance);

        Assert.DoesNotThrowAsync(async () => await handle.DisposeAsync(),
            "dispose must not surface a courtesy cancel that failed on a closed channel");

        channel.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Test]
    public async Task TopicSubscriptionHandle_DisposesConsumerAndBindChannels_WhenTheConsumerCancelThrows()
    {
        var consumerChannel = ClosedConsumerChannel();
        var bindChannel = new Mock<IChannel>();
        bindChannel.SetupGet(c => c.IsOpen).Returns(true);
        bindChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask).Verifiable();
        var connection = new Mock<IConnection>();
        connection.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bindChannel.Object);

        var handle = new RabbitMqClient.TopicSubscriptionHandle(
            connection.Object, consumerChannel.Object, "tag", "queue", "exchange", NullLogger.Instance);
        await handle.AddBindingAsync("run.*"); // materialises the lazy bind channel

        Assert.DoesNotThrowAsync(async () => await handle.DisposeAsync());

        Assert.Multiple(() =>
        {
            consumerChannel.Verify(c => c.DisposeAsync(), Times.Once, "the consumer channel is disposed");
            bindChannel.Verify(c => c.DisposeAsync(), Times.Once,
                "the bind channel is disposed even though the consumer cancel threw");
        });
    }

    [Test]
    public async Task TopicSubscriptionHandle_DisposesTheBindChannel_WhenTheConsumerChannelDisposeThrows()
    {
        var consumerChannel = new Mock<IChannel>();
        consumerChannel.Setup(c => c.DisposeAsync())
            .Returns(ValueTask.FromException(new InvalidOperationException("channel already closed")));
        var bindChannel = new Mock<IChannel>();
        bindChannel.SetupGet(c => c.IsOpen).Returns(true);
        bindChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask).Verifiable();
        var connection = new Mock<IConnection>();
        connection.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bindChannel.Object);

        var handle = new RabbitMqClient.TopicSubscriptionHandle(
            connection.Object, consumerChannel.Object, "tag", "queue", "exchange", NullLogger.Instance);
        await handle.AddBindingAsync("run.*");

        Assert.CatchAsync<InvalidOperationException>(async () => await handle.DisposeAsync(),
            "a failing channel dispose is the caller's to see");
        bindChannel.Verify(c => c.DisposeAsync(), Times.Once, "...but the bind channel is released first");
    }
}

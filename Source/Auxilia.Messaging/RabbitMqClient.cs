using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Auxilia.Messaging;

/// <summary>
///     Production RabbitMQ implementation of <see cref="IMessageBusClient" />.
///     Messages are JSON-serialised with System.Text.Json.
/// </summary>
public sealed class RabbitMqClient : IMessageBusClient, IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _publishChannel;

    private RabbitMqClient(IConnection connection, IChannel publishChannel)
    {
        _connection = connection;
        _publishChannel = publishChannel;
    }

    public async ValueTask DisposeAsync()
    {
        await _publishChannel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public async Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        await _publishChannel.QueueDeclareAsync(
            queueName,
            true,
            false,
            false,
            null,
            cancellationToken: cancellationToken);
    }

    public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = new BasicProperties { Persistent = true, ContentType = "application/json" };
        await _publishChannel.BasicPublishAsync(
            string.Empty,
            topic,
            false,
            props,
            body,
            cancellationToken);
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(
        string queueName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            var message = JsonSerializer.Deserialize<T>(body);
            if (message is not null)
                await handler(message, CancellationToken.None);
            await channel.BasicAckAsync(ea.DeliveryTag, false);
        };
        var consumerTag = await channel.BasicConsumeAsync(queueName, false, consumer, cancellationToken);
        return new SubscriptionHandle(channel, consumerTag);
    }

    public static async Task<RabbitMqClient> CreateAsync(string hostName, int port = 5672,
        string userName = "guest", string password = "guest",
        CancellationToken cancellationToken = default)
    {
        var factory = new ConnectionFactory
        {
            HostName = hostName,
            Port = port,
            UserName = userName,
            Password = password,
            AutomaticRecoveryEnabled = true
        };
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        return new RabbitMqClient(connection, channel);
    }

    private sealed class SubscriptionHandle(IChannel channel, string consumerTag) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await channel.BasicCancelAsync(consumerTag);
            await channel.DisposeAsync();
        }
    }
}
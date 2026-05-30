using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Auxilia.Messaging;

/// <summary>
///     Production RabbitMQ implementation of <see cref="IMessageBusClient" />.
///     Messages are JSON-serialised with System.Text.Json.
///     Spans and metrics are recorded via <see cref="MessagingTelemetry" />.
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

    public async Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
    {
        await _publishChannel.ExchangeDeclareAsync(
            exchangeName,
            "fanout",
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
    }

    public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        using var activity = MessagingTelemetry.ActivitySource.StartActivity(
            "rabbitmq.publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination", topic);
        activity?.SetTag("messaging.operation", "publish");

        // Propagate W3C trace context into message headers
        var headers = new Dictionary<string, object?>();
        Propagators.DefaultTextMapPropagator.Inject(
            new PropagationContext(
                activity?.Context ?? Activity.Current?.Context ?? default,
                Baggage.Current),
            headers,
            static (carrier, key, value) => carrier[key] = value);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            Headers = headers
        };

        await _publishChannel.BasicPublishAsync(
            string.Empty,
            topic,
            false,
            props,
            body,
            cancellationToken);

        MessagingTelemetry.PublishCounter.Add(1, new TagList { { "messaging.topic", topic } });
    }

    public async Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
    {
        using var activity = MessagingTelemetry.ActivitySource.StartActivity(
            "rabbitmq.publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination", exchangeName);
        activity?.SetTag("messaging.operation", "publish");

        var headers = new Dictionary<string, object?>();
        Propagators.DefaultTextMapPropagator.Inject(
            new PropagationContext(
                activity?.Context ?? Activity.Current?.Context ?? default,
                Baggage.Current),
            headers,
            static (carrier, key, value) => carrier[key] = value);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            Headers = headers
        };

        await _publishChannel.BasicPublishAsync(
            exchangeName,
            string.Empty,
            false,
            props,
            body,
            cancellationToken);

        MessagingTelemetry.PublishCounter.Add(1, new TagList { { "messaging.topic", exchangeName } });
    }

    public async Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
        string exchangeName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        var queueDeclareResult = await channel.QueueDeclareAsync(
            string.Empty, false, true, true, null, cancellationToken: cancellationToken);
        var queueName = queueDeclareResult.QueueName;
        await channel.QueueBindAsync(queueName, exchangeName, string.Empty, null, cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var parentContext = Propagators.DefaultTextMapPropagator.Extract(
                default,
                ea.BasicProperties.Headers,
                static (headers, key) =>
                {
                    if (headers == null || !headers.TryGetValue(key, out var val))
                        return [];
                    var str = val is byte[] bytes ? Encoding.UTF8.GetString(bytes) : val?.ToString();
                    return str is null ? [] : [str];
                });

            using var activity = MessagingTelemetry.ActivitySource.StartActivity(
                "rabbitmq.consume",
                ActivityKind.Consumer,
                parentContext.ActivityContext);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination", exchangeName);
            activity?.SetTag("messaging.operation", "receive");

            MessagingTelemetry.ReceiveCounter.Add(1, new TagList { { "messaging.queue", queueName } });

            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            var msg = JsonSerializer.Deserialize<T>(body);
            if (msg is not null)
                await handler(msg, CancellationToken.None);
            await channel.BasicAckAsync(ea.DeliveryTag, false);
        };
        var consumerTag = await channel.BasicConsumeAsync(queueName, false, consumer, cancellationToken);
        return new SubscriptionHandle(channel, consumerTag);
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
            // Extract W3C trace context from message headers
            var parentContext = Propagators.DefaultTextMapPropagator.Extract(
                default,
                ea.BasicProperties.Headers,
                static (headers, key) =>
                {
                    if (headers == null || !headers.TryGetValue(key, out var val))
                        return [];
                    var str = val is byte[] bytes ? Encoding.UTF8.GetString(bytes) : val?.ToString();
                    return str is null ? [] : [str];
                });

            using var activity = MessagingTelemetry.ActivitySource.StartActivity(
                "rabbitmq.consume",
                ActivityKind.Consumer,
                parentContext.ActivityContext);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination", queueName);
            activity?.SetTag("messaging.operation", "receive");

            MessagingTelemetry.ReceiveCounter.Add(1, new TagList { { "messaging.queue", queueName } });

            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            var msg = JsonSerializer.Deserialize<T>(body);
            if (msg is not null)
                await handler(msg, CancellationToken.None);
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
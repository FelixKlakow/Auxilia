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
            Headers = headers,
            // Tag the CLR message type so fanout subscribers can reject messages that
            // aren't theirs: a fanout exchange delivers every message to every bound queue,
            // and lenient JSON deserialization would otherwise silently coerce a mismatched
            // command into a record with null fields.
            Type = message?.GetType().FullName
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

    public async Task DeclareTopicExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
    {
        await _publishChannel.ExchangeDeclareAsync(
            exchangeName,
            "topic",
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
    }

    public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
        => PublishToExchangeCoreAsync(exchangeName, string.Empty, message, cancellationToken);

    public Task PublishToTopicExchangeAsync<T>(
        string exchangeName, string routingKey, T message, CancellationToken cancellationToken = default)
        => PublishToExchangeCoreAsync(exchangeName, routingKey, message, cancellationToken);

    private async Task PublishToExchangeCoreAsync<T>(
        string exchangeName, string routingKey, T message, CancellationToken cancellationToken)
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
            Headers = headers,
            // Tag the CLR message type so fanout subscribers can reject messages that
            // aren't theirs: a fanout exchange delivers every message to every bound queue,
            // and lenient JSON deserialization would otherwise silently coerce a mismatched
            // command into a record with null fields.
            Type = message?.GetType().FullName
        };

        await _publishChannel.BasicPublishAsync(
            exchangeName,
            routingKey,
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

            // A fanout exchange delivers a copy of every message to every bound queue, so a
            // consumer typed as T can receive a message of a different type. Skip anything
            // whose tagged type doesn't match T rather than let lenient deserialization
            // fabricate a partially-null record. Untagged messages (Type == null) are
            // processed as before, for backward compatibility.
            var messageType = ea.BasicProperties.Type;
            if (messageType is not null && messageType != typeof(T).FullName)
            {
                await channel.BasicAckAsync(ea.DeliveryTag, false);
                return;
            }

            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            var msg = JsonSerializer.Deserialize<T>(body);
            if (msg is not null)
                await handler(msg, CancellationToken.None);
            await channel.BasicAckAsync(ea.DeliveryTag, false);
        };
        var consumerTag = await channel.BasicConsumeAsync(queueName, false, consumer, cancellationToken);
        return new SubscriptionHandle(channel, consumerTag);
    }

    public async Task<IAsyncDisposable> SubscribeToExchangeSharedAsync<T>(
        string exchangeName,
        string queueName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        // A durable named queue bound to the fanout: every subscriber sharing the name competes
        // for the same deliveries — the multi-node mirror pattern (each event processed once).
        var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queueName, durable: true, exclusive: false, autoDelete: false, arguments: null,
            cancellationToken: cancellationToken);
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

            // Same type-tag guard as every other consumer: the fanout delivers everything.
            var messageType = ea.BasicProperties.Type;
            if (messageType is not null && messageType != typeof(T).FullName)
            {
                await channel.BasicAckAsync(ea.DeliveryTag, false);
                return;
            }

            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            var msg = JsonSerializer.Deserialize<T>(body);
            if (msg is not null)
                await handler(msg, CancellationToken.None);
            await channel.BasicAckAsync(ea.DeliveryTag, false);
        };
        var consumerTag = await channel.BasicConsumeAsync(queueName, false, consumer, cancellationToken);
        return new SubscriptionHandle(channel, consumerTag);
    }

    public async Task<ITopicSubscription> SubscribeToTopicExchangeAsync<T>(
        string exchangeName,
        IReadOnlyCollection<string> routingKeys,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        var queueDeclareResult = await channel.QueueDeclareAsync(
            string.Empty, false, true, true, null, cancellationToken: cancellationToken);
        var queueName = queueDeclareResult.QueueName;
        foreach (var routingKey in routingKeys)
            await channel.QueueBindAsync(queueName, exchangeName, routingKey, null, cancellationToken: cancellationToken);

        var consumerTag = await AttachConsumerAsync(channel, queueName, exchangeName, handler, cancellationToken);
        return new TopicSubscriptionHandle(channel, consumerTag, queueName, exchangeName);
    }

    public async Task<IAsyncDisposable> SubscribeToTopicExchangeSharedAsync<T>(
        string exchangeName,
        string queueName,
        string bindingKey,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        // A durable named queue bound to the topic exchange (mirrors bind "#"): every subscriber
        // sharing the name competes for the same deliveries — each event processed once fleet-wide.
        var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queueName, durable: true, exclusive: false, autoDelete: false, arguments: null,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queueName, exchangeName, bindingKey, null, cancellationToken: cancellationToken);

        var consumerTag = await AttachConsumerAsync(channel, queueName, exchangeName, handler, cancellationToken);
        return new SubscriptionHandle(channel, consumerTag);
    }

    private static async Task<string> AttachConsumerAsync<T>(
        IChannel channel,
        string queueName,
        string exchangeName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
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

            // Same type-tag guard as every other consumer: multiple message types can share
            // an exchange, and lenient deserialization must not fabricate partially-null records.
            var messageType = ea.BasicProperties.Type;
            if (messageType is not null && messageType != typeof(T).FullName)
            {
                await channel.BasicAckAsync(ea.DeliveryTag, false);
                return;
            }

            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            var msg = JsonSerializer.Deserialize<T>(body);
            if (msg is not null)
                await handler(msg, CancellationToken.None);
            await channel.BasicAckAsync(ea.DeliveryTag, false);
        };
        return await channel.BasicConsumeAsync(queueName, false, consumer, cancellationToken);
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

            // A fanout exchange delivers a copy of every message to every bound queue, so a
            // consumer typed as T can receive a message of a different type. Skip anything
            // whose tagged type doesn't match T rather than let lenient deserialization
            // fabricate a partially-null record. Untagged messages (Type == null) are
            // processed as before, for backward compatibility.
            var messageType = ea.BasicProperties.Type;
            if (messageType is not null && messageType != typeof(T).FullName)
            {
                await channel.BasicAckAsync(ea.DeliveryTag, false);
                return;
            }

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

    private sealed class TopicSubscriptionHandle(
        IChannel channel, string consumerTag, string queueName, string exchangeName) : ITopicSubscription
    {
        // Binding mutations arrive from concurrent SSE opens/closes; a channel is not safe for
        // concurrent operations, so they are serialized here.
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task AddBindingAsync(string routingKey, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await channel.QueueBindAsync(
                    queueName, exchangeName, routingKey, null, cancellationToken: cancellationToken);
            }
            finally { _gate.Release(); }
        }

        public async Task RemoveBindingAsync(string routingKey, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await channel.QueueUnbindAsync(
                    queueName, exchangeName, routingKey, null, cancellationToken);
            }
            finally { _gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await channel.BasicCancelAsync(consumerTag);
            await channel.DisposeAsync();
            _gate.Dispose();
        }
    }
}
using Auxilia.Messaging;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.Messaging;

/// <summary>
/// Consumer ack/nack resilience against a real broker. The in-memory fake has no delivery
/// semantics at all, so only here can we prove that a throwing handler nacks its delivery
/// (requeue once, then drop as poison) instead of leaving it unacked until channel close,
/// and that a corrupt body is dropped explicitly rather than acked as handled.
/// </summary>
[TestFixture]
[Category("System")]
public class MessageBusConsumerResilienceSystemTests
{
    private const string RabbitMqImage = "rabbitmq:3.13-management";

    private RabbitMqContainer _rabbitMq = null!;
    private RabbitMqClient _client = null!;

    public sealed record WorkCommand(string Id, string Payload);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername("guest")
            .WithPassword("guest")
            .Build();
        await _rabbitMq.StartAsync();
        _client = await RabbitMqClient.CreateAsync(
            _rabbitMq.Hostname, _rabbitMq.GetMappedPublicPort(5672));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_rabbitMq is not null)
        {
            await _rabbitMq.StopAsync();
            await _rabbitMq.DisposeAsync();
        }
    }

    [Test]
    public async Task ThrowingHandler_IsRedeliveredExactlyOnce_ThenDropped_AndTheConsumerStaysAlive()
    {
        var queue = $"resilience-{Guid.NewGuid():N}";
        await _client.DeclareQueueAsync(queue);

        var gate = new object();
        var poisonAttempts = 0;
        var handled = new List<WorkCommand>();

        await using var subscription = await _client.SubscribeAsync<WorkCommand>(queue, (m, _) =>
        {
            lock (gate)
            {
                if (m.Id == "poison")
                {
                    poisonAttempts++;
                    throw new InvalidOperationException("deterministic handler failure");
                }
                handled.Add(m);
            }
            return Task.CompletedTask;
        });

        // First delivery throws -> nack with requeue; the redelivery throws again -> nack
        // WITHOUT requeue (poison). Exactly two attempts, never a third.
        await _client.PublishAsync(queue, new WorkCommand("poison", "boom"));
        await WaitUntilAsync(() => { lock (gate) { return poisonAttempts >= 2; } }, TimeSpan.FromSeconds(10));
        await Task.Delay(1000);

        lock (gate)
            Assert.That(poisonAttempts, Is.EqualTo(2),
                "a throwing handler gets its message redelivered exactly once, then the poison is dropped");

        // The consumer must still be alive and the queue drained: a healthy message flows.
        await _client.PublishAsync(queue, new WorkCommand("good", "payload"));
        await WaitUntilAsync(() => { lock (gate) { return handled.Count == 1; } }, TimeSpan.FromSeconds(10));

        lock (gate)
        {
            Assert.That(handled.Select(m => m.Id), Is.EqualTo(new[] { "good" }),
                "after dropping the poison the subscription keeps processing");
            Assert.That(poisonAttempts, Is.EqualTo(2), "the poison must not resurface");
        }
    }

    [Test]
    public async Task NullBody_IsDroppedWithoutRequeue_AndTheConsumerStaysAlive()
    {
        var queue = $"null-body-{Guid.NewGuid():N}";
        await _client.DeclareQueueAsync(queue);

        var gate = new object();
        var handled = new List<WorkCommand>();

        await using var subscription = await _client.SubscribeAsync<WorkCommand>(queue, (m, _) =>
        {
            lock (gate) { handled.Add(m); }
            return Task.CompletedTask;
        });

        // Serializes to the literal "null" -> deserializes to null -> must be nacked without
        // requeue (never handed to the handler, never looping), leaving the consumer healthy.
        await _client.PublishAsync<WorkCommand>(queue, null!);
        await _client.PublishAsync(queue, new WorkCommand("good", "payload"));

        await WaitUntilAsync(() => { lock (gate) { return handled.Count == 1; } }, TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        lock (gate)
            Assert.That(handled.Select(m => m.Id), Is.EqualTo(new[] { "good" }),
                "a null-deserializing body is dropped, and the next message is still processed");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
    }
}

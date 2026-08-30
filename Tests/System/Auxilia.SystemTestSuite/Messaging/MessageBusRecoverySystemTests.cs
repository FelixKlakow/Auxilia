using Auxilia.Messaging;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.Messaging;

/// <summary>
/// Connection-recovery behavior of the selective topic subscription against a real broker.
/// The regression being guarded: with a SERVER-named queue, automatic topology recovery
/// re-declares the queue under a NEW broker name, so every later Add/RemoveBinding on the
/// captured stale name 404s and closes the bind channel — after a broker blip every new SSE
/// run/view/artifact stream would bind nothing until process restart. The client now uses a
/// client-generated queue name (recovered under the SAME name) and lazily replaces a closed
/// bind channel. This fixture gets its OWN container because it force-closes every
/// connection server-side.
/// </summary>
[TestFixture]
[Category("System")]
public class MessageBusRecoverySystemTests
{
    private const string RabbitMqImage = "rabbitmq:3.13-management";

    private RabbitMqContainer _rabbitMq = null!;
    private RabbitMqClient _client = null!;

    public sealed record RunEvent(string RunId, string State);

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
    public async Task TopicSubscription_BindingMutations_StillWork_AfterConnectionRecovery()
    {
        var exchange = $"topic-recovery-{Guid.NewGuid():N}";
        await _client.DeclareTopicExchangeAsync(exchange);

        var gate = new object();
        var received = new List<RunEvent>();

        await using var subscription = await _client.SubscribeToTopicExchangeAsync<RunEvent>(
            exchange, ["run-a"], (m, _) =>
            {
                lock (gate) { received.Add(m); }
                return Task.CompletedTask;
            });

        // Baseline: the initial binding delivers.
        await _client.PublishToTopicExchangeAsync(exchange, "run-a", new RunEvent("run-a", "before-close"));
        await WaitUntilAsync(
            () => { lock (gate) { return received.Any(m => m.State == "before-close"); } },
            TimeSpan.FromSeconds(10));
        lock (gate)
            Assert.That(received.Any(m => m.State == "before-close"), Is.True,
                "the initial binding must deliver before the forced close");

        // Sever every connection server-side; AutomaticRecoveryEnabled reconnects and topology
        // recovery must re-declare the CLIENT-named queue and re-apply its recorded bindings.
        await _rabbitMq.ExecAsync(["rabbitmqctl", "close_all_connections", "forced-by-recovery-test"]);

        // Wait for recovery by probing the still-bound key until a probe arrives. Publishes
        // during the outage throw or vanish with the dead connection — both are expected.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var recovered = false;
        while (DateTime.UtcNow < deadline && !recovered)
        {
            try
            {
                await _client.PublishToTopicExchangeAsync(exchange, "run-a", new RunEvent("run-a", "probe"));
            }
            catch
            {
                // Connection still recovering.
            }
            await Task.Delay(500);
            lock (gate) { recovered = received.Any(m => m.State == "probe"); }
        }
        Assert.That(recovered, Is.True,
            "the pre-existing binding must deliver again once the connection has recovered");

        // The defect scenario: a NEW binding after recovery. On the stale server-named queue
        // this 404'd, closed the bind channel, and every later mutation threw AlreadyClosedException.
        await subscription.AddBindingAsync("run-b");
        await _client.PublishToTopicExchangeAsync(exchange, "run-b", new RunEvent("run-b", "after-recovery"));
        await WaitUntilAsync(
            () => { lock (gate) { return received.Any(m => m.State == "after-recovery"); } },
            TimeSpan.FromSeconds(10));
        lock (gate)
            Assert.That(received.Any(m => m.State == "after-recovery"), Is.True,
                "a binding added AFTER recovery must deliver — the stale-queue-name regression");

        // And removal keeps working too: the feed on the removed key stops.
        await subscription.RemoveBindingAsync("run-b");
        await _client.PublishToTopicExchangeAsync(exchange, "run-b", new RunEvent("run-b", "after-unbind"));
        await Task.Delay(500);
        lock (gate)
            Assert.That(received.Any(m => m.State == "after-unbind"), Is.False,
                "a binding removed after recovery must stop delivering");
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

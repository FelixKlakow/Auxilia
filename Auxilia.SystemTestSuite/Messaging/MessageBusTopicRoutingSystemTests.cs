using Auxilia.Messaging;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.Messaging;

/// <summary>
/// Selective event routing against a real broker. The in-memory fake over-delivers by design
/// (its topic methods degrade to fanout), so key matching, the two-word status-key scheme
/// (<c>{instanceId}.{commandId}</c> matched by <c>{id}.#</c> / <c>*.{id}</c>, with <c>#</c>
/// matching zero words), dynamic Add/RemoveBinding, and the <c>#</c>-bound shared mirror can
/// only be verified here.
/// </summary>
[TestFixture]
[Category("System")]
public class MessageBusTopicRoutingSystemTests
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
    public async Task TopicExchange_DeliversOnlyMatchingKeys_AndHashMatchesZeroWords()
    {
        var exchange = $"topic-routing-{Guid.NewGuid():N}";
        await _client.DeclareTopicExchangeAsync(exchange);

        var gate = new object();
        var runA = new List<RunEvent>();
        var mirror = new List<RunEvent>();

        // The SSE-publisher pattern: bind run A's keys the way RunStreamPublisher does —
        // "A.#" must also match the SINGLE-word key "A" (# matches zero words).
        await using var selective = await _client.SubscribeToTopicExchangeAsync<RunEvent>(
            exchange, ["run-a.#", "*.run-a"], (m, _) =>
            {
                lock (gate) { runA.Add(m); }
                return Task.CompletedTask;
            });
        // The mirror pattern: "#" sees everything.
        await using var mirrorSub = await _client.SubscribeToTopicExchangeAsync<RunEvent>(
            exchange, ["#"], (m, _) =>
            {
                lock (gate) { mirror.Add(m); }
                return Task.CompletedTask;
            });

        await _client.PublishToTopicExchangeAsync(exchange, "run-a", new RunEvent("run-a", "single-word"));
        await _client.PublishToTopicExchangeAsync(exchange, "run-b.run-a", new RunEvent("run-b", "second-word"));
        await _client.PublishToTopicExchangeAsync(exchange, "run-b", new RunEvent("run-b", "other-run"));

        await WaitUntilAsync(() => { lock (gate) { return mirror.Count == 3; } }, TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        lock (gate)
        {
            Assert.Multiple(() =>
            {
                Assert.That(runA.Select(m => m.State), Is.EquivalentTo(new[] { "single-word", "second-word" }),
                    "'run-a.#' must match the bare key 'run-a' and '*.run-a' the second word — nothing else");
                Assert.That(mirror, Has.Count.EqualTo(3), "the '#' mirror sees the full feed");
            });
        }
    }

    [Test]
    public async Task Bindings_CanBeAddedAndRemoved_WhileConsuming()
    {
        var exchange = $"topic-dynamic-{Guid.NewGuid():N}";
        await _client.DeclareTopicExchangeAsync(exchange);

        var gate = new object();
        var received = new List<RunEvent>();

        await using var subscription = await _client.SubscribeToTopicExchangeAsync<RunEvent>(
            exchange, [], (m, _) =>
            {
                lock (gate) { received.Add(m); }
                return Task.CompletedTask;
            });

        // No bindings yet — nothing arrives.
        await _client.PublishToTopicExchangeAsync(exchange, "run-a", new RunEvent("run-a", "before-bind"));
        await Task.Delay(500);
        lock (gate)
            Assert.That(received, Is.Empty, "an unbound subscription must receive nothing");

        // Bind (an SSE stream opened) — events flow.
        await subscription.AddBindingAsync("run-a");
        await _client.PublishToTopicExchangeAsync(exchange, "run-a", new RunEvent("run-a", "while-bound"));
        await WaitUntilAsync(() => { lock (gate) { return received.Count == 1; } }, TimeSpan.FromSeconds(10));

        // Unbind (the stream closed) — the feed stops.
        await subscription.RemoveBindingAsync("run-a");
        await _client.PublishToTopicExchangeAsync(exchange, "run-a", new RunEvent("run-a", "after-unbind"));
        await Task.Delay(500);

        lock (gate)
        {
            Assert.That(received.Select(m => m.State), Is.EqualTo(new[] { "while-bound" }),
                "only events published while the binding existed may arrive");
        }
    }

    [Test]
    public async Task SharedTopicSubscription_BindsHash_AndCompetesForEachMessage()
    {
        var exchange = $"topic-shared-{Guid.NewGuid():N}";
        var sharedQueue = $"mirror-{Guid.NewGuid():N}";
        await _client.DeclareTopicExchangeAsync(exchange);

        var gate = new object();
        var nodeA = new List<RunEvent>();
        var nodeB = new List<RunEvent>();

        await using var subA = await _client.SubscribeToTopicExchangeSharedAsync<RunEvent>(
            exchange, sharedQueue, "#", (m, _) =>
            {
                lock (gate) { nodeA.Add(m); }
                return Task.CompletedTask;
            });
        await using var subB = await _client.SubscribeToTopicExchangeSharedAsync<RunEvent>(
            exchange, sharedQueue, "#", (m, _) =>
            {
                lock (gate) { nodeB.Add(m); }
                return Task.CompletedTask;
            });

        const int published = 20;
        for (var i = 0; i < published; i++)
            await _client.PublishToTopicExchangeAsync(exchange, $"run-{i}", new RunEvent($"run-{i}", "s"));

        await WaitUntilAsync(() =>
        {
            lock (gate) { return nodeA.Count + nodeB.Count >= published; }
        }, TimeSpan.FromSeconds(15));
        await Task.Delay(500);

        lock (gate)
        {
            Assert.Multiple(() =>
            {
                Assert.That(nodeA.Count + nodeB.Count, Is.EqualTo(published),
                    "the '#'-bound shared mirror must process each event exactly once in total");
                Assert.That(nodeA.Concat(nodeB).Select(m => m.RunId).Distinct().Count(),
                    Is.EqualTo(published), "no event may be processed twice across the nodes");
            });
        }
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

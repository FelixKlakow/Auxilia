using Auxilia.Messaging;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.Messaging;

/// <summary>
/// Regression coverage for fanout-exchange type isolation. A fanout delivers a copy of every
/// message to every bound queue, so a subscriber typed as <c>T</c> receives messages of other
/// types too. <see cref="RabbitMqClient"/> tags each message with its CLR type and each
/// subscriber must ignore anything that is not its own type — otherwise lenient JSON
/// deserialization silently coerces a mismatched command into a record with null fields, which
/// is what corrupted the slot-configuration store when a slot-instance upsert fanned out to the
/// slot-configuration handler. The in-memory test fake dispatches by <c>is T</c>, so this seam
/// only reproduces against a real broker.
/// </summary>
[TestFixture]
[Category("System")]
public class MessageBusFanoutSystemTests
{
    private const string RabbitMqImage = "rabbitmq:3.13-management";

    private RabbitMqContainer _rabbitMq = null!;
    private RabbitMqClient _client = null!;

    // Overlap on ProviderType only; a BetaCommand deserialized as AlphaCommand yields
    // WorkflowType == null and SlotName == null — the exact corruption being guarded against.
    public sealed record AlphaCommand(string WorkflowType, string SlotName, string ProviderType);
    public sealed record BetaCommand(string Name, string ProviderType, string Scope);

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
    public async Task FanoutExchange_DeliversOnlyToTheMatchingTypedSubscriber()
    {
        var exchange = $"fanout-type-isolation-{Guid.NewGuid():N}";
        await _client.DeclareExchangeAsync(exchange);

        var gate = new object();
        var alpha = new List<AlphaCommand>();
        var beta = new List<BetaCommand>();

        await _client.SubscribeToExchangeAsync<AlphaCommand>(exchange, (m, _) =>
        {
            lock (gate) { alpha.Add(m); }
            return Task.CompletedTask;
        });
        await _client.SubscribeToExchangeAsync<BetaCommand>(exchange, (m, _) =>
        {
            lock (gate) { beta.Add(m); }
            return Task.CompletedTask;
        });

        // A BetaCommand must reach only the Beta subscriber — never the Alpha one.
        await _client.PublishToExchangeAsync(exchange,
            new BetaCommand("workspace", "coding-session-workspace", "Personal"));

        await WaitUntilAsync(() => { lock (gate) { return beta.Count == 1; } }, TimeSpan.FromSeconds(10));
        // Let any erroneous cross-delivery arrive before asserting it did not.
        await Task.Delay(500);

        lock (gate)
        {
            Assert.That(beta, Has.Count.EqualTo(1), "Beta subscriber should receive its own message.");
            Assert.That(beta[0].ProviderType, Is.EqualTo("coding-session-workspace"));
            Assert.That(alpha, Is.Empty,
                "Alpha subscriber must not receive a BetaCommand — that is the fanout type cross-talk.");
        }

        // Positive path: an AlphaCommand still reaches Alpha and not Beta.
        await _client.PublishToExchangeAsync(exchange,
            new AlphaCommand("claude-code", "coding-agent", "claude-code-cli"));

        await WaitUntilAsync(() => { lock (gate) { return alpha.Count == 1; } }, TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        lock (gate)
        {
            Assert.That(alpha, Has.Count.EqualTo(1), "Alpha subscriber should receive its own message.");
            Assert.That(alpha[0].WorkflowType, Is.EqualTo("claude-code"));
            Assert.That(beta, Has.Count.EqualTo(1), "Beta subscriber must not receive an AlphaCommand.");
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

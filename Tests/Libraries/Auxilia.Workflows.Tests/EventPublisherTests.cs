using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Events;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class EventPublisherTests
{
    private sealed record ReviewReady(string Verdict, int Score);

    private static readonly Guid InstanceId = Guid.NewGuid();

    private RecordingBus _bus = null!;
    private DefaultEventPublisher _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new RecordingBus();
        _sut = new DefaultEventPublisher(_bus, InstanceId, "producer-wf", new List<EventDescriptor>
        {
            new("review-ready"),
            new("nightly-clean")
        });
    }

    [Test]
    public async Task PublishAsync_DeclaredEvent_PublishesWorkflowEventMessageKeyedByType()
    {
        await _sut.PublishAsync("review-ready", new ReviewReady("approve", 9), workItemId: "WI-7");

        Assert.That(_bus.PublishedToTopicExchange, Has.Count.EqualTo(1));
        var (exchange, routingKey, message) = _bus.PublishedToTopicExchange[0];
        var evt = (WorkflowEventMessage)message;
        Assert.Multiple(() =>
        {
            Assert.That(exchange, Is.EqualTo(WorkflowEventMessage.ExchangeName));
            Assert.That(routingKey, Is.EqualTo("review-ready"));
            Assert.That(evt.EventType, Is.EqualTo("review-ready"));
            Assert.That(evt.WorkflowInstanceId, Is.EqualTo(InstanceId));
            Assert.That(evt.WorkflowType, Is.EqualTo("producer-wf"));
            Assert.That(evt.WorkItemId, Is.EqualTo("WI-7"));
            Assert.That(evt.EventId, Is.Not.EqualTo(Guid.Empty));
        });
    }

    [Test]
    public async Task PublishAsync_PayloadJson_RoundTripsThePayload()
    {
        var payload = new ReviewReady("approve", 9);

        await _sut.PublishAsync("review-ready", payload);

        var evt = (WorkflowEventMessage)_bus.PublishedToTopicExchange[0].Message;
        var roundTripped = JsonSerializer.Deserialize<ReviewReady>(evt.PayloadJson!);
        Assert.That(roundTripped, Is.EqualTo(payload));
    }

    [Test]
    public async Task PublishAsync_Payloadless_PublishesNullPayloadAndEmptyWorkItem()
    {
        await _sut.PublishAsync("nightly-clean");

        var evt = (WorkflowEventMessage)_bus.PublishedToTopicExchange[0].Message;
        Assert.Multiple(() =>
        {
            Assert.That(evt.PayloadJson, Is.Null);
            Assert.That(evt.WorkItemId, Is.Empty);
        });
    }

    [Test]
    public void PublishAsync_UndeclaredEvent_ThrowsInvalidOperationException()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.PublishAsync("not-declared"));
        Assert.That(ex!.Message, Does.Contain("not-declared"));
    }

    [Test]
    public void PublishAsync_ReservedPlatformPrefix_IsRefused()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.PublishAsync("run.succeeded"));
        Assert.That(ex!.Message, Does.Contain("reserved"));
        Assert.That(_bus.PublishedToTopicExchange, Is.Empty);
    }

    [Test]
    public void PublishAsync_OversizedPayload_IsRefused()
    {
        var oversized = new { data = new string('x', DefaultEventPublisher.MaxPayloadBytes + 1) };
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.PublishAsync("review-ready", oversized));
        Assert.That(ex!.Message, Does.Contain("artifact"));
        Assert.That(_bus.PublishedToTopicExchange, Is.Empty);
    }

    [Test]
    public async Task PublishAsync_WildcardCharactersInTheType_AreNeutralizedInTheRoutingKey()
    {
        _sut = new DefaultEventPublisher(_bus, InstanceId, "producer-wf",
            [new EventDescriptor("odd*type#name")]);

        await _sut.PublishAsync("odd*type#name");

        Assert.That(_bus.PublishedToTopicExchange[0].RoutingKey, Is.EqualTo("odd-type-name"),
            "a type name can never widen a topic binding");
    }

    [Test]
    public async Task PublishAsync_MultiplePublishes_DeclaresExchangeOnce()
    {
        await _sut.PublishAsync("nightly-clean");
        await _sut.PublishAsync("nightly-clean");

        Assert.That(_bus.DeclaredExchanges, Is.EqualTo(new[] { WorkflowEventMessage.ExchangeName }));
    }

    [Test]
    public void DeclaresEvent_ExportsTheSchema_AndRefusesReservedAndDuplicateTypes()
    {
        var builder = WorkflowBuilder.Create("wf")
            .DeclaresEvent<ReviewReady>("review-ready", "a review finished")
            .DeclaresEvent("nightly-clean");

        var schema = builder.BuildSchema();
        Assert.Multiple(() =>
        {
            Assert.That(schema.Events.Select(e => e.EventType),
                Is.EqualTo(new[] { "review-ready", "nightly-clean" }));
            Assert.That(schema.Events[0].PayloadSchemaJson, Is.Not.Null.And.Contain("verdict").IgnoreCase);
            Assert.That(schema.Events[1].PayloadSchemaJson, Is.Null);
        });

        Assert.Throws<InvalidOperationException>(() => builder.DeclaresEvent("review-ready"));
        Assert.Throws<InvalidOperationException>(() => builder.DeclaresEvent("run.failed"));
    }

    private sealed class RecordingBus : IMessageBusClient
    {
        public List<string> DeclaredExchanges { get; } = [];
        public List<(string Exchange, string RoutingKey, object Message)> PublishedToTopicExchange { get; } = [];

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeclareTopicExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
        {
            DeclaredExchanges.Add(exchangeName);
            return Task.CompletedTask;
        }

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishToTopicExchangeAsync<T>(
            string exchangeName, string routingKey, T message, CancellationToken cancellationToken = default)
        {
            PublishedToTopicExchange.Add((exchangeName, routingKey, message!));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(new NoopDisposable());

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
            string exchangeName, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(new NoopDisposable());

        private sealed class NoopDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

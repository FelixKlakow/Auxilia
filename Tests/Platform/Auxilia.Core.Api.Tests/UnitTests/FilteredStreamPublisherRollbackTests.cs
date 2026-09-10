using Auxilia.Core.Api.Services;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// The filtered-stream publishers (<see cref="ArtifactStreamPublisher"/>,
/// <see cref="EventStreamPublisher"/>) keep one refcount per binding key. A subscribe cancelled
/// while queued at the bind gate joined no audience — its rollback must not decrement the count
/// a sibling stream holds, or that sibling's key is unbound under it.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class FilteredStreamPublisherRollbackTests
{
    [Test]
    public async Task ArtifactSubscribe_CancelledAtTheGate_LeavesTheSiblingsKeyBound()
    {
        var bus = new GatedBindingBus();
        var broker = new ArtifactStreamBroker();
        var publisher = new ArtifactStreamPublisher(bus, broker, NullLogger<ArtifactStreamPublisher>.Instance);
        await publisher.StartAsync(CancellationToken.None);
        try
        {
            await AssertSiblingSurvivesAsync(
                bus, ct => broker.SubscribeAsync("plan", null, ct),
                ArtifactPersistedEvent.RoutingKeyFor("plan"));
        }
        finally { await publisher.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task EventSubscribe_CancelledAtTheGate_LeavesTheSiblingsKeyBound()
    {
        var bus = new GatedBindingBus();
        var broker = new EventStreamBroker();
        var publisher = new EventStreamPublisher(bus, broker, NullLogger<EventStreamPublisher>.Instance);
        await publisher.StartAsync(CancellationToken.None);
        try
        {
            await AssertSiblingSurvivesAsync(
                bus, ct => broker.SubscribeAsync("review-ready", null, ct),
                WorkflowEventMessage.RoutingKeyFor("review-ready"));
        }
        finally { await publisher.StopAsync(CancellationToken.None); }
    }

    private static async Task AssertSiblingSurvivesAsync<TSubscription>(
        GatedBindingBus bus, Func<CancellationToken, Task<TSubscription>> subscribe, string expectedKey)
        where TSubscription : IDisposable
    {
        var bindEntered = new TaskCompletionSource();
        var releaseBind = new TaskCompletionSource();
        bus.BeforeBind = _ =>
        {
            bindEntered.TrySetResult();
            return releaseBind.Task;
        };

        var first = subscribe(CancellationToken.None);
        await bindEntered.Task;

        using var cts = new CancellationTokenSource();
        var second = subscribe(cts.Token);
        cts.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await second);

        bus.BeforeBind = null;
        releaseBind.SetResult();
        using var subscription = await first;
        await Task.Delay(200);

        Assert.That(bus.BoundKeys, Is.EquivalentTo(new[] { expectedKey }),
            "the surviving subscriber's key must stay bound");

        subscription.Dispose();
        await Task.Delay(50);
        Assert.That(bus.BoundKeys, Is.Empty, "the real unsubscribe still releases the key");
    }

    /// <summary>Single-exchange fake bus recording bound keys, with a hook to hold AddBinding open.</summary>
    private sealed class GatedBindingBus : IMessageBusClient
    {
        private readonly HashSet<string> _bound = new(StringComparer.Ordinal);

        public Func<string, Task>? BeforeBind { get; set; }

        public IReadOnlySet<string> BoundKeys
        {
            get { lock (_bound) return new HashSet<string>(_bound, StringComparer.Ordinal); }
        }

        public Task<ITopicSubscription> SubscribeToTopicExchangeAsync<T>(
            string exchangeName, IReadOnlyCollection<string> routingKeys,
            Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
            => Task.FromResult<ITopicSubscription>(new GatedSubscription(this));

        private sealed class GatedSubscription(GatedBindingBus bus) : ITopicSubscription
        {
            public async Task AddBindingAsync(string routingKey, CancellationToken cancellationToken = default)
            {
                if (bus.BeforeBind is { } gate)
                    await gate(routingKey);
                lock (bus._bound)
                    bus._bound.Add(routingKey);
            }

            public Task RemoveBindingAsync(string routingKey, CancellationToken cancellationToken = default)
            {
                lock (bus._bound)
                    bus._bound.Remove(routingKey);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
            string exchangeName, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

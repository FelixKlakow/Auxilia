using Auxilia.Core.Api.Services;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Verifies the selective-routing bookkeeping of <see cref="RunStreamPublisher"/>: bus bindings
/// exist exactly while a run has an SSE audience, and a command-id audience gains the instance-id
/// alias bindings once the pairing is known. Real key matching is covered by the RabbitMQ
/// system test — here the fake only records which keys were bound.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunStreamPublisherBindingTests
{
    private BindingRecordingBus _bus = null!;
    private RunStreamBroker _broker = null!;
    private RunStreamPublisher _publisher = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new BindingRecordingBus();
        _broker = new RunStreamBroker();
        _publisher = new RunStreamPublisher(
            _bus, _broker,
            new Auxilia.UniversalDataAccess.Implementations.InMemoryDataAccess<Auxilia.Core.Api.Data.CoreRunRecord>(),
            NullLogger<RunStreamPublisher>.Instance);
        await _publisher.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public Task TearDown() => _publisher.StopAsync(CancellationToken.None);

    private IReadOnlySet<string> StatusKeys => _bus.BoundKeys(WorkflowStatusEvent.ExchangeName);
    private IReadOnlySet<string> ViewKeys => _bus.BoundKeys(ViewDataMessage.ExchangeName);

    [Test]
    public async Task OpenStream_BindsTheRunsKeys_AndCloseRemovesThem()
    {
        var runId = Guid.NewGuid();

        var subscription = await _broker.SubscribeAsync(runId);

        Assert.Multiple(() =>
        {
            Assert.That(StatusKeys, Is.EquivalentTo(new[] { $"{runId}.#", $"*.{runId}" }));
            Assert.That(ViewKeys, Is.EquivalentTo(new[] { runId.ToString() }));
        });

        subscription.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(StatusKeys, Is.Empty);
            Assert.That(ViewKeys, Is.Empty);
        });
    }

    [Test]
    public async Task SecondSubscriberOfTheSameRun_KeepsBindingsUntilTheLastCloses()
    {
        var runId = Guid.NewGuid();

        var first = await _broker.SubscribeAsync(runId);
        var second = await _broker.SubscribeAsync(runId);
        first.Dispose();

        Assert.That(StatusKeys, Is.Not.Empty, "one open stream must keep the run bound");

        second.Dispose();
        Assert.That(StatusKeys, Is.Empty);
    }

    [Test]
    public async Task CommandIdAudience_GainsInstanceAliasBindings_WhenTheClaimArrives()
    {
        var commandId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        using var subscription = await _broker.SubscribeAsync(commandId);
        await _bus.DeliverAsync(WorkflowStatusEvent.ExchangeName, new WorkflowStatusEvent(
            instanceId, "wf", "Received", null, DateTimeOffset.UtcNow, CommandId: commandId));

        Assert.Multiple(() =>
        {
            Assert.That(StatusKeys, Does.Contain($"{instanceId}.#").And.Contain($"*.{instanceId}"),
                "the instance id must be bound so instance-keyed events reach the command-id audience");
            Assert.That(ViewKeys, Does.Contain(instanceId.ToString()),
                "views are instance-keyed only — without the alias binding they would never arrive");
        });
    }

    [Test]
    public async Task TerminalTransition_DropsTheAliasBindings_ButKeepsTheAudienceBindings()
    {
        var commandId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        using var subscription = await _broker.SubscribeAsync(commandId);
        await _bus.DeliverAsync(WorkflowStatusEvent.ExchangeName, new WorkflowStatusEvent(
            instanceId, "wf", "Received", null, DateTimeOffset.UtcNow, CommandId: commandId));
        await _bus.DeliverAsync(WorkflowStatusEvent.ExchangeName, new WorkflowStatusEvent(
            instanceId, "wf", "Success", null, DateTimeOffset.UtcNow));

        Assert.Multiple(() =>
        {
            Assert.That(StatusKeys, Is.EquivalentTo(new[] { $"{commandId}.#", $"*.{commandId}" }),
                "a terminal run's alias bindings are dead weight and must be removed");
            Assert.That(ViewKeys, Is.EquivalentTo(new[] { commandId.ToString() }));
        });
    }

    [Test]
    public async Task RegisteredAlias_BindsTheInstance_ForALateCommandIdSubscriber()
    {
        var commandId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        // The endpoint resolves the pairing from the run store when the claim happened long ago.
        await _publisher.RegisterAliasAsync(commandId, instanceId);
        using var subscription = await _broker.SubscribeAsync(commandId);

        Assert.That(ViewKeys, Does.Contain(instanceId.ToString()));
    }

    [Test]
    public async Task SubscribeCancelledWhileWaitingForTheGate_LeavesTheSiblingsKeysBound()
    {
        // The first subscriber holds the bind gate mid-RPC; a second subscriber of the same run
        // is cancelled while queued at the gate. Its rollback must not decrement an audience it
        // never joined — that would unbind the first subscriber's keys under it.
        var runId = Guid.NewGuid();
        var bindEntered = new TaskCompletionSource();
        var releaseBind = new TaskCompletionSource();
        _bus.BeforeBind = _ =>
        {
            bindEntered.TrySetResult();
            return releaseBind.Task;
        };

        var first = _broker.SubscribeAsync(runId);
        await bindEntered.Task;

        using var cts = new CancellationTokenSource();
        var second = _broker.SubscribeAsync(runId, cts.Token);
        cts.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(() => second);

        _bus.BeforeBind = null;
        releaseBind.SetResult();
        using var subscription = await first;
        // Let a rogue fire-and-forget unbind land before asserting.
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(StatusKeys, Is.EquivalentTo(new[] { $"{runId}.#", $"*.{runId}" }),
                "the surviving subscriber's status keys must stay bound");
            Assert.That(ViewKeys, Is.EquivalentTo(new[] { runId.ToString() }));
        });

        subscription.Dispose();
        await Task.Delay(50);
        Assert.That(StatusKeys, Is.Empty, "the real unsubscribe still releases the keys");
    }

    /// <summary>
    /// Fake bus that records topic bindings per exchange and lets tests deliver events to the
    /// registered handlers regardless of bindings (the broker filters; routing is the broker's
    /// job and is system-tested against RabbitMQ).
    /// </summary>
    private sealed class BindingRecordingBus : IMessageBusClient
    {
        private readonly Dictionary<string, HashSet<string>> _bindings = new();
        private readonly Dictionary<string, Func<object, CancellationToken, Task>> _handlers = new();

        /// <summary>Awaited before every AddBinding — lets a test hold the publisher's bind gate open.</summary>
        public Func<string, Task>? BeforeBind { get; set; }

        public IReadOnlySet<string> BoundKeys(string exchangeName)
        {
            lock (_bindings)
                return _bindings.TryGetValue(exchangeName, out var keys) ? new HashSet<string>(keys) : new HashSet<string>();
        }

        public Task DeliverAsync<T>(string exchangeName, T message)
            => _handlers[exchangeName](message!, CancellationToken.None);

        public Task<ITopicSubscription> SubscribeToTopicExchangeAsync<T>(
            string exchangeName, IReadOnlyCollection<string> routingKeys,
            Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
        {
            lock (_bindings)
                _bindings[exchangeName] = new HashSet<string>(routingKeys, StringComparer.Ordinal);
            _handlers[exchangeName] = (msg, ct) => handler((T)msg, ct);
            return Task.FromResult<ITopicSubscription>(new RecordingSubscription(this, exchangeName));
        }

        private sealed class RecordingSubscription(BindingRecordingBus bus, string exchangeName) : ITopicSubscription
        {
            public async Task AddBindingAsync(string routingKey, CancellationToken cancellationToken = default)
            {
                if (bus.BeforeBind is { } gate)
                    await gate(routingKey);
                lock (bus._bindings)
                    bus._bindings[exchangeName].Add(routingKey);
            }

            public Task RemoveBindingAsync(string routingKey, CancellationToken cancellationToken = default)
            {
                lock (bus._bindings)
                    bus._bindings[exchangeName].Remove(routingKey);
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

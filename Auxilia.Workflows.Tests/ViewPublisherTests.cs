using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class ViewPublisherTests
{
    private sealed record ProgressItem(string Step, int Percent);

    private static readonly Guid InstanceId = Guid.NewGuid();

    private RecordingBus _bus = null!;
    private DefaultViewPublisher _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new RecordingBus();
        _sut = new DefaultViewPublisher(_bus, InstanceId, new List<ViewDescriptor>
        {
            new("progress", "{}", ViewRendering.Stream, ViewLifecycle.LiveAndPersisted),
            new("log", "{}", ViewRendering.Log, ViewLifecycle.Live)
        });
    }

    [Test]
    public async Task PublishAsync_DeclaredView_PublishesViewDataMessageToViewDataExchange()
    {
        await _sut.PublishAsync("progress", new ProgressItem("clone", 10));

        Assert.That(_bus.PublishedToExchange, Has.Count.EqualTo(1));
        var (exchange, message) = _bus.PublishedToExchange[0];
        Assert.That(exchange, Is.EqualTo(ViewDataMessage.ExchangeName));
        var viewData = (ViewDataMessage)message;
        Assert.That(viewData.WorkflowInstanceId, Is.EqualTo(InstanceId));
        Assert.That(viewData.ViewName, Is.EqualTo("progress"));
    }

    [Test]
    public async Task PublishAsync_TwoItems_AssignsMonotonicSequenceStartingAtOne()
    {
        await _sut.PublishAsync("progress", new ProgressItem("clone", 10));
        await _sut.PublishAsync("progress", new ProgressItem("build", 50));

        var sequences = _bus.PublishedToExchange
            .Select(p => ((ViewDataMessage)p.Message).Sequence)
            .ToList();
        Assert.That(sequences, Is.EqualTo(new long[] { 1, 2 }));
    }

    [Test]
    public async Task PublishAsync_PayloadJson_RoundTripsTheItem()
    {
        var item = new ProgressItem("test", 75);

        await _sut.PublishAsync("progress", item);

        var viewData = (ViewDataMessage)_bus.PublishedToExchange[0].Message;
        var roundTripped = JsonSerializer.Deserialize<ProgressItem>(viewData.PayloadJson);
        Assert.That(roundTripped, Is.EqualTo(item));
    }

    [Test]
    public void PublishAsync_UndeclaredView_ThrowsInvalidOperationException()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.PublishAsync("not-declared", new ProgressItem("x", 0)));
        Assert.That(ex!.Message, Does.Contain("not-declared"));
    }

    [Test]
    public async Task PublishAsync_TwoViews_SequencesArePerView()
    {
        await _sut.PublishAsync("progress", new ProgressItem("clone", 10));
        await _sut.PublishAsync("progress", new ProgressItem("build", 50));
        await _sut.PublishAsync("log", new ProgressItem("first log line", 0));

        var logMessage = _bus.PublishedToExchange
            .Select(p => (ViewDataMessage)p.Message)
            .Single(m => m.ViewName == "log");
        Assert.That(logMessage.Sequence, Is.EqualTo(1),
            "Each view must carry its own sequence counter.");
    }

    [Test]
    public async Task PublishAsync_MultiplePublishes_DeclaresExchangeOnce()
    {
        await _sut.PublishAsync("progress", new ProgressItem("clone", 10));
        await _sut.PublishAsync("progress", new ProgressItem("build", 50));

        Assert.That(_bus.DeclaredExchanges, Is.EqualTo(new[] { ViewDataMessage.ExchangeName }));
    }

    private sealed class RecordingBus : IMessageBusClient
    {
        public List<string> DeclaredExchanges { get; } = [];
        public List<(string Exchange, object Message)> PublishedToExchange { get; } = [];

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
        {
            DeclaredExchanges.Add(exchangeName);
            return Task.CompletedTask;
        }

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
        {
            PublishedToExchange.Add((exchangeName, message!));
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

using Auxilia.Messaging;
using Auxilia.Workflows.Companions;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class PodControlClientTests
{
    private const string PodControlQueue = "runner-pod-control";
    private const string ResponseTopic = "workflow-pod-response-test";

    private static readonly Guid InstanceId = Guid.NewGuid();
    private const string InstanceToken = "instance-token-123";

    private RecordingBus _bus = null!;
    private PodControlClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new RecordingBus();
        _client = new PodControlClient(_bus, InstanceId, InstanceToken, PodControlQueue, ResponseTopic);
        await _client.StartAsync();
    }

    [TearDown]
    public async Task TearDown() => await _client.DisposeAsync();

    private void RespondToRequests(Func<PodControlRequest, PodControlResponse> respond)
    {
        _bus.OnPublish = (topic, message) =>
        {
            if (topic == PodControlQueue && message is PodControlRequest req)
                _bus.DeliverAsync(ResponseTopic, respond(req));
        };
    }

    [Test]
    public async Task SpawnAsync_PublishesRequestWithIdentityAndToken_AndReturnsTheCompanion()
    {
        RespondToRequests(req => new PodControlResponse(
            req.RequestId, true, null, """{"name":"machine-r1","endpoint":"machine-r1:5000"}"""));

        var companion = await _client.SpawnAsync(new CompanionSpec("machine-r1", "sim-base"));

        var requests = _bus.Published(PodControlQueue);
        Assert.That(requests, Has.Count.EqualTo(1));
        var request = (PodControlRequest)requests[0];
        Assert.Multiple(() =>
        {
            Assert.That(request.WorkflowInstanceId, Is.EqualTo(InstanceId));
            Assert.That(request.Action, Is.EqualTo(PodControlRequest.Spawn));
            Assert.That(request.SpecJson, Does.Contain("machine-r1").And.Contain("sim-base"));
            Assert.That(request.InstanceToken, Is.EqualTo(InstanceToken));
            Assert.That(request.RequestId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(companion.Name, Is.EqualTo("machine-r1"));
            Assert.That(companion.Endpoint, Is.EqualTo("machine-r1:5000"));
        });
    }

    [Test]
    public async Task StopAsync_PublishesStopRequest_CorrelatedByRequestId()
    {
        RespondToRequests(req => new PodControlResponse(req.RequestId, true, null, null));

        await _client.StopAsync("machine-r1");

        var request = (PodControlRequest)_bus.Published(PodControlQueue).Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.Action, Is.EqualTo(PodControlRequest.Stop));
            Assert.That(request.SpecJson, Does.Contain("machine-r1"));
            Assert.That(request.WorkflowInstanceId, Is.EqualTo(InstanceId));
            Assert.That(request.InstanceToken, Is.EqualTo(InstanceToken));
        });
    }

    [Test]
    public void SpawnAsync_ResponseWithForeignRequestId_IsIgnoredAndCallTimesOut()
    {
        _client.CallTimeout = TimeSpan.FromMilliseconds(200);
        RespondToRequests(_ => new PodControlResponse(Guid.NewGuid(), true, null, "{}"));

        Assert.ThrowsAsync<TimeoutException>(() => _client.SpawnAsync(new CompanionSpec("m", "base")));
    }

    [Test]
    public void SpawnAsync_NoResponse_TimesOut()
    {
        _client.CallTimeout = TimeSpan.FromMilliseconds(200);

        var ex = Assert.ThrowsAsync<TimeoutException>(() => _client.SpawnAsync(new CompanionSpec("m", "base")));
        Assert.That(ex!.Message, Does.Contain("spawn"));
    }

    [Test]
    public void SpawnAsync_CallerCancelledWhileWaiting_ThrowsOperationCanceled_NotTimeout()
    {
        _client.CallTimeout = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(50);

        Assert.ThrowsAsync<OperationCanceledException>(
            () => _client.SpawnAsync(new CompanionSpec("m", "base"), cts.Token));
    }

    [Test]
    public void SpawnAsync_LateResponseAfterTimeout_IsIgnored_PendingEntryWasCleanedUp()
    {
        _client.CallTimeout = TimeSpan.FromMilliseconds(200);

        Assert.ThrowsAsync<TimeoutException>(() => _client.SpawnAsync(new CompanionSpec("m", "base")));

        // The timed-out call removed its pending entry: a late response correlated to the very
        // RequestId that timed out must find nothing to complete and be swallowed silently.
        var request = (PodControlRequest)_bus.Published(PodControlQueue).Single();
        Assert.DoesNotThrow(() => _bus.DeliverAsync(
            ResponseTopic, new PodControlResponse(request.RequestId, true, null, "{}")));
    }

    [Test]
    public void SpawnAsync_FailureResponse_ThrowsWithActionAndReason()
    {
        RespondToRequests(req => new PodControlResponse(req.RequestId, false, "envelope exceeded", null));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.SpawnAsync(new CompanionSpec("m", "base")));
        Assert.That(ex!.Message, Does.Contain("spawn"));
        Assert.That(ex.Message, Does.Contain("envelope exceeded"));
    }

    [Test]
    public void SpawnAsync_SuccessWithNullCompanionPayload_ThrowsTheNullGuard()
    {
        // JSON "null" deserializes to a null SpawnedCompanion — the client's null-guard throws.
        RespondToRequests(req => new PodControlResponse(req.RequestId, true, null, "null"));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.SpawnAsync(new CompanionSpec("m", "base")));
        Assert.That(ex!.Message, Does.Contain("returned no companion"));
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class RecordingBus : IMessageBusClient
    {
        private readonly Dictionary<string, List<object>> _published = new();
        private readonly Dictionary<string, Func<object, CancellationToken, Task>> _handlers = new();

        public Action<string, object>? OnPublish { get; set; }

        public List<object> Published(string topic)
            => _published.TryGetValue(topic, out var list) ? list : [];

        public void DeliverAsync<T>(string topic, T message)
            where T : notnull
        {
            if (_handlers.TryGetValue(topic, out var handler))
                handler(message, CancellationToken.None).GetAwaiter().GetResult();
        }

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            _handlers[queueName] = (msg, ct) => handler((T)msg, ct);
            return Task.FromResult<IAsyncDisposable>(NoopDisposable.Instance);
        }

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
            string exchangeName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
            => SubscribeAsync(exchangeName, handler, cancellationToken);

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
        {
            if (!_published.ContainsKey(topic))
                _published[topic] = new List<object>();
            _published[topic].Add(message!);
            OnPublish?.Invoke(topic, message!);
            return Task.CompletedTask;
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
            => PublishAsync(exchangeName, message, cancellationToken);
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class ResourceProxyClientTests
{
    private const string ProxyQueue = "workflow-resource-proxy";
    private const string ResponseTopic = "workflow-response-test";

    private static readonly Guid InstanceId = Guid.NewGuid();
    private const string InstanceToken = "instance-token-123";

    private RecordingBus _bus = null!;
    private ResourceProxyClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new RecordingBus();
        _client = new ResourceProxyClient(_bus, InstanceId, InstanceToken, ProxyQueue, ResponseTopic);
        await _client.StartAsync();
    }

    [TearDown]
    public async Task TearDown() => await _client.DisposeAsync();

    private void RespondToRequests(Func<ResourceRequest, ResourceResponse> respond)
    {
        _bus.OnPublish = (topic, message) =>
        {
            if (topic == ProxyQueue && message is ResourceRequest req)
                _bus.DeliverAsync(ResponseTopic, respond(req));
        };
    }

    [Test]
    public async Task CallAsync_PublishesRequestWithIdentityAndToken()
    {
        RespondToRequests(req => new ResourceResponse(req.RequestId, true, null, "{}"));

        await _client.CallAsync("task-source", "create-comment", """{"text":"hi"}""");

        var requests = _bus.Published(ProxyQueue);
        Assert.That(requests, Has.Count.EqualTo(1));
        var request = (ResourceRequest)requests[0];
        Assert.Multiple(() =>
        {
            Assert.That(request.WorkflowInstanceId, Is.EqualTo(InstanceId));
            Assert.That(request.ResourceName, Is.EqualTo("task-source"));
            Assert.That(request.Operation, Is.EqualTo("create-comment"));
            Assert.That(request.PayloadJson, Is.EqualTo("""{"text":"hi"}"""));
            Assert.That(request.InstanceToken, Is.EqualTo(InstanceToken));
            Assert.That(request.RequestId, Is.Not.EqualTo(Guid.Empty));
        });
    }

    [Test]
    public async Task CallAsync_CorrelatesResponseByRequestId()
    {
        RespondToRequests(req => new ResourceResponse(
            req.RequestId, true, null, $"\"{req.RequestId:D}\""));

        var first = await _client.CallAsync("task-source", "op-1", "{}");
        var second = await _client.CallAsync("task-source", "op-2", "{}");

        var requests = _bus.Published(ProxyQueue).Cast<ResourceRequest>().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(requests[0].RequestId, Is.Not.EqualTo(requests[1].RequestId));
            Assert.That(first, Is.EqualTo($"\"{requests[0].RequestId:D}\""));
            Assert.That(second, Is.EqualTo($"\"{requests[1].RequestId:D}\""));
        });
    }

    [Test]
    public void CallAsync_ResponseWithForeignRequestId_IsIgnoredAndCallTimesOut()
    {
        _client.CallTimeout = TimeSpan.FromMilliseconds(200);
        RespondToRequests(_ => new ResourceResponse(Guid.NewGuid(), true, null, "{}"));

        Assert.ThrowsAsync<TimeoutException>(() => _client.CallAsync("task-source", "op", "{}"));
    }

    [Test]
    public void CallAsync_NoResponse_TimesOut()
    {
        _client.CallTimeout = TimeSpan.FromMilliseconds(200);

        var ex = Assert.ThrowsAsync<TimeoutException>(() => _client.CallAsync("task-source", "op", "{}"));
        Assert.That(ex!.Message, Does.Contain("task-source:op"));
    }

    [Test]
    public void CallAsync_FailureResponse_ThrowsWithResourceOperationAndReason()
    {
        RespondToRequests(req => new ResourceResponse(req.RequestId, false, "backend unavailable", null));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.CallAsync("task-source", "create-comment", "{}"));
        Assert.That(ex!.Message, Does.Contain("task-source:create-comment"));
        Assert.That(ex.Message, Does.Contain("backend unavailable"));
    }

    [Test]
    public async Task CallAsync_SuccessWithNullResultJson_ReturnsEmptyString()
    {
        RespondToRequests(req => new ResourceResponse(req.RequestId, true, null, null));

        var result = await _client.CallAsync("task-source", "op", "{}");

        Assert.That(result, Is.EqualTo(string.Empty));
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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class SlotActivatorTests
{
    private const string ActivationQueue = "workflow-slot-activation";
    private const string ResponseTopic = "workflow-response-test";

    private static readonly Guid InstanceId = Guid.NewGuid();
    private const string InstanceToken = "instance-token-123";

    private RecordingBus _bus = null!;
    private EphemeralKeyPair _keyPair = null!;
    private SlotActivator _activator = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new RecordingBus();
        _keyPair = new EphemeralKeyPair();
        _activator = new SlotActivator(
            _bus, _keyPair, InstanceId, InstanceToken, ActivationQueue, ResponseTopic);
        await _activator.StartAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _activator.DisposeAsync();
        _keyPair.Dispose();
    }

    /// <summary>Encrypts the settings dictionary for the requester's public key (OAEP-SHA256).</summary>
    private static EncryptedSlotConfiguration EncryptFor(
        string publicKeyBase64, string providerType, Dictionary<string, string> settings)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        var cipher = rsa.Encrypt(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings)),
            RSAEncryptionPadding.OaepSHA256);
        return new EncryptedSlotConfiguration(providerType, Convert.ToBase64String(cipher));
    }

    private void RespondToActivationRequests(Dictionary<string, string> settings)
    {
        _bus.OnPublish = (topic, message) =>
        {
            if (topic == ActivationQueue && message is SlotActivationRequest req)
            {
                var slot = EncryptFor(req.PublicKey, "stub-provider", settings);
                _bus.DeliverAsync(ResponseTopic, new SlotActivationResponse(
                    req.WorkflowInstanceId, req.SlotName, true, null, slot,
                    DateTimeOffset.UtcNow.AddMinutes(30)));
            }
        };
    }

    [Test]
    public async Task FetchAsync_PublishesActivationRequestWithIdentityAndKey()
    {
        RespondToActivationRequests(new Dictionary<string, string> { ["k"] = "v" });

        await _activator.FetchAsync("slot1");

        var requests = _bus.Published(ActivationQueue);
        Assert.That(requests, Has.Count.EqualTo(1));
        var request = (SlotActivationRequest)requests[0];
        Assert.Multiple(() =>
        {
            Assert.That(request.WorkflowInstanceId, Is.EqualTo(InstanceId));
            Assert.That(request.SlotName, Is.EqualTo("slot1"));
            Assert.That(request.PublicKey, Is.EqualTo(_keyPair.PublicKeyBase64));
            Assert.That(request.InstanceToken, Is.EqualTo(InstanceToken));
        });
    }

    [Test]
    public async Task FetchAsync_DecryptsConfigurationWithTheEphemeralKey()
    {
        RespondToActivationRequests(new Dictionary<string, string> { ["ApiKey"] = "super-secret" });

        var configuration = await _activator.FetchAsync("slot1");

        Assert.Multiple(() =>
        {
            Assert.That(configuration.ProviderType, Is.EqualTo("stub-provider"));
            Assert.That(configuration.Settings["ApiKey"], Is.EqualTo("super-secret"));
        });
    }

    [Test]
    public async Task FetchAsync_EachSlotIsAnIndividualRequest()
    {
        RespondToActivationRequests(new Dictionary<string, string> { ["k"] = "v" });

        await _activator.FetchAsync("slot1");
        await _activator.FetchAsync("slot2");

        var requests = _bus.Published(ActivationQueue).Cast<SlotActivationRequest>().ToList();
        Assert.That(requests.Select(r => r.SlotName), Is.EqualTo(new[] { "slot1", "slot2" }));
    }

    [Test]
    public void FetchAsync_FailureResponse_ThrowsWithSlotNameAndReason()
    {
        _bus.OnPublish = (topic, message) =>
        {
            if (topic == ActivationQueue && message is SlotActivationRequest req)
                _bus.DeliverAsync(ResponseTopic, new SlotActivationResponse(
                    req.WorkflowInstanceId, req.SlotName, false, "no configuration", null));
        };

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => _activator.FetchAsync("slot1"));
        Assert.That(ex!.Message, Does.Contain("slot1"));
        Assert.That(ex.Message, Does.Contain("no configuration"));
    }

    [Test]
    public void FetchAsync_NoResponse_TimesOut()
    {
        _activator.ActivationTimeout = TimeSpan.FromMilliseconds(200);

        Assert.ThrowsAsync<TimeoutException>(() => _activator.FetchAsync("slot1"));
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

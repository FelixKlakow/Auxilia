using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class DefaultSignalEmitterTests
{
    private sealed record TestPayload(string Key, int Number);

    [Test]
    public async Task DefaultSignalEmitter_EmitAsync_PublishesWorkflowSignalMessage_WithCorrectSignalName()
    {
        var bus = new CapturingBus();
        var context = new WorkflowInstanceContext(Guid.NewGuid());
        var emitter = new DefaultSignalEmitter(bus, context);

        await emitter.EmitAsync("my-signal", new TestPayload("hello", 42));

        Assert.That(bus.Published, Has.Count.EqualTo(1));
        var msg = (WorkflowSignalMessage)bus.Published[0].Message;
        Assert.That(msg.SignalName, Is.EqualTo("my-signal"));
    }

    [Test]
    public async Task DefaultSignalEmitter_EmitAsync_PayloadJson_IsValidJson_ContainingPayloadProperties()
    {
        var bus = new CapturingBus();
        var context = new WorkflowInstanceContext(Guid.NewGuid());
        var emitter = new DefaultSignalEmitter(bus, context);

        var payload = new TestPayload("value", 99);
        await emitter.EmitAsync("signal", payload);

        var msg = (WorkflowSignalMessage)bus.Published[0].Message;
        var roundTripped = JsonSerializer.Deserialize<TestPayload>(msg.PayloadJson);

        Assert.That(roundTripped, Is.Not.Null);
        Assert.That(roundTripped!.Key, Is.EqualTo("value"));
        Assert.That(roundTripped.Number, Is.EqualTo(99));
    }

    [Test]
    public async Task DefaultSignalEmitter_EmitAsync_WorkflowInstanceId_MatchesContextInstanceId()
    {
        var instanceId = Guid.NewGuid();
        var bus = new CapturingBus();
        var context = new WorkflowInstanceContext(instanceId);
        var emitter = new DefaultSignalEmitter(bus, context);

        await emitter.EmitAsync("signal", new TestPayload("x", 1));

        var msg = (WorkflowSignalMessage)bus.Published[0].Message;
        Assert.That(msg.WorkflowInstanceId, Is.EqualTo(instanceId));
    }

    [Test]
    public void WorkflowBootstrapper_Apply_RegistersISignalEmitter()
    {
        using var keyPair = new EphemeralKeyPair();
        var response = new WorkflowConfigurationResponse(
            Guid.NewGuid(), true, null,
            new Dictionary<string, EncryptedSlotConfiguration>());

        var services = new ServiceCollection();
        services.AddSingleton<IMessageBusClient>(new CapturingBus());

        var bootstrapper = new WorkflowBootstrapper(response, keyPair, new SlotHandlerResolver(), [], Guid.NewGuid());
        bootstrapper.Apply(services);

        var provider = services.BuildServiceProvider();
        var emitter = provider.GetService<ISignalEmitter>();

        Assert.That(emitter, Is.Not.Null);
        Assert.That(emitter, Is.InstanceOf<DefaultSignalEmitter>());
    }

    [Test]
    public void WorkflowBootstrapper_Apply_RegistersWorkflowInstanceContext_WithCorrectInstanceId()
    {
        using var keyPair = new EphemeralKeyPair();
        var instanceId = Guid.NewGuid();
        var response = new WorkflowConfigurationResponse(
            instanceId, true, null,
            new Dictionary<string, EncryptedSlotConfiguration>());

        var services = new ServiceCollection();
        services.AddSingleton<IMessageBusClient>(new CapturingBus());

        var bootstrapper = new WorkflowBootstrapper(response, keyPair, new SlotHandlerResolver(), [], instanceId);
        bootstrapper.Apply(services);

        var provider = services.BuildServiceProvider();
        var ctx = provider.GetRequiredService<WorkflowInstanceContext>();

        Assert.That(ctx.InstanceId, Is.EqualTo(instanceId));
    }

    [Test]
    public async Task WorkflowBuilder_Run_DeclaresWorkflowSignalsQueue()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.Run));
            else if (topic == "workflow-registration" && message is WorkflowRegistrationRequest req)
                bus.DeliverAsync(req.ResponseTopic,
                    new WorkflowConfigurationResponse(req.WorkflowInstanceId, true, null,
                        new Dictionary<string, EncryptedSlotConfiguration>()));
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.Run([], context);

        Assert.That(bus.DeclaredQueues, Does.Contain("workflow.signals"));
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class CapturingBus : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, message!));
            return Task.CompletedTask;
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
        {
            Published.Add((exchangeName, message!));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(string queueName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(NoopDisposable.Instance);

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(string exchangeName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(NoopDisposable.Instance);
    }

    private sealed class RecordingBus : IMessageBusClient
    {
        private readonly Dictionary<string, List<object>> _published = new();
        private readonly Dictionary<string, Func<object, CancellationToken, Task>> _handlers = new();

        public List<string> DeclaredQueues { get; } = [];
        public Action<string, object>? OnPublish { get; set; }

        public List<object> Published(string topic)
            => _published.TryGetValue(topic, out var list) ? list : [];

        public void DeliverAsync<T>(string topic, T message) where T : notnull
        {
            if (_handlers.TryGetValue(topic, out var handler))
                handler(message, CancellationToken.None).GetAwaiter().GetResult();
        }

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
        {
            DeclaredQueues.Add(queueName);
            return Task.CompletedTask;
        }

        public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync<T>(string queueName,
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
                _published[topic] = [];
            _published[topic].Add(message!);
            OnPublish?.Invoke(topic, message!);
            return Task.CompletedTask;
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
            => PublishAsync(exchangeName, message, cancellationToken);
    }

    private sealed class FakeWorkflowRunContext(IMessageBusClient bus, IProcessExitService exitService)
        : IWorkflowRunContext
    {
        public IMessageBusClient MessageBus { get; } = bus;
        public IProcessExitService ExitService { get; } = exitService;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

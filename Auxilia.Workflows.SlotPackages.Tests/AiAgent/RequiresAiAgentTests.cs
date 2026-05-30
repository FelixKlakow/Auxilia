using Auxilia.Messaging;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
[Category("Unit")]
public class RequiresAiAgentTests
{
    [Test]
    public async Task RequiresAiAgent_AddsSlot_WithCorrectName()
    {
        var bus = new SchemaCapturingBus();
        var context = new FakeRunContext(bus);
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresAiAgent("ai-agent", new AiCapabilities
        {
            MinContextWindow = 8192,
            SupportedModalities = [Modality.Text]
        });
        var wb = (WorkflowBuilder)builder;
        wb._directiveTimeout = TimeSpan.FromSeconds(5);

        await wb.RunAsync([], context);

        var schema = ((WorkflowSchemaMessage)bus.Published("workflow.schema")[0]).Schema;
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema!.Slots, Has.Count.EqualTo(1));
        Assert.That(schema.Slots[0].SlotName, Is.EqualTo("ai-agent"));
        Assert.That(schema.Slots[0].ServiceType, Is.EqualTo(typeof(IAiAgent)));
    }

    private sealed class SchemaCapturingBus : IMessageBusClient
    {
        private readonly Dictionary<string, List<object>> _published = new();
        private readonly Dictionary<string, Func<object, CancellationToken, Task>> _handlers = new();

        public List<object> Published(string topic)
            => _published.TryGetValue(topic, out var list) ? list : [];

        public void Deliver<T>(string topic, T message) where T : notnull
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

            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
                Deliver(ann.ResponseTopic, new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.EmitSchema));

            return Task.CompletedTask;
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
            => PublishAsync(exchangeName, message, cancellationToken);
    }

    private sealed class FakeRunContext(IMessageBusClient bus) : IWorkflowRunContext
    {
        public IMessageBusClient MessageBus { get; } = bus;
        public IProcessExitService ExitService { get; } = new NoopExitService();
        public ILogger Logger { get; } = NullLogger.Instance;
    }

    private sealed class NoopExitService : IProcessExitService
    {
        public void Exit(int code) { }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}


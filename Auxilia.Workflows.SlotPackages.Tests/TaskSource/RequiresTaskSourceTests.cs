using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Workflows.SlotPackages.Tests.TaskSource;

[TestFixture]
[Category("Unit")]
public class RequiresTaskSourceTests
{
    [Test]
    public async Task RequiresTaskSource_RecordsSlot_InSchemaOutput()
    {
        var bus = new SchemaCapturingBus();
        var context = new FakeRunContext(bus);
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresTaskSource("task-source", new TaskSourceCapabilities
        {
            SupportedItemTypes = [ItemType.UserStory, ItemType.Bug]
        });
        var wb = (WorkflowBuilder)builder;
        wb._directiveTimeout = TimeSpan.FromSeconds(5);

        await wb.RunAsync([], context);

        var schema = ((WorkflowSchemaMessage)bus.Published("workflow.schema")[0]).Schema;
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema!.Slots, Has.Count.EqualTo(1));
        Assert.That(schema.Slots[0].SlotName, Is.EqualTo("task-source"));
    }

    [Test]
    public async Task RequiresTaskSource_ReturnsBuilder_ForFluency()
    {
        var builder = WorkflowBuilder.Create("test");
        var returned = builder.RequiresTaskSource("ts", new TaskSourceCapabilities
        {
            SupportedItemTypes = [ItemType.Feature]
        });
        Assert.That(returned, Is.SameAs(builder));
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

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            _handlers[queueName] = (msg, ct) => handler((T)msg, ct);
            return Task.FromResult<IAsyncDisposable>(NoopDisposable.Instance);
        }

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
        {
            if (!_published.ContainsKey(topic))
                _published[topic] = new List<object>();
            _published[topic].Add(message!);

            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
                Deliver(ann.ResponseTopic, new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.EmitSchema));

            return Task.CompletedTask;
        }
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

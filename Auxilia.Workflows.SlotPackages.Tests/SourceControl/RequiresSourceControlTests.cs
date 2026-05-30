using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.SourceControl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Workflows.SlotPackages.Tests.SourceControl;

[TestFixture]
[Category("Unit")]
public class RequiresSourceControlTests
{
    [Test]
    public async Task RequiresSourceControl_RecordsSlot_InSchemaOutput()
    {
        var bus = new SchemaCapturingBus();
        var context = new FakeRunContext(bus);
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresSourceControl("sc-reader", new SourceControlCapabilities
        {
            RequiredPermissions = [Permission.Read]
        });
        var wb = (WorkflowBuilder)builder;
        wb._directiveTimeout = TimeSpan.FromSeconds(5);

        await wb.RunAsync([], context);

        var schema = ((WorkflowSchemaMessage)bus.Published("workflow.schema")[0]).Schema;
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema!.Slots, Has.Count.EqualTo(1));
        Assert.That(schema.Slots[0].SlotName, Is.EqualTo("sc-reader"));
        Assert.That(schema.Slots[0].ServiceType, Is.EqualTo(typeof(ISourceControlAccess)));
    }

    [Test]
    public async Task RequiresSourceControl_TwoCalls_AccumulatesBothSlots()
    {
        var bus = new SchemaCapturingBus();
        var context = new FakeRunContext(bus);
        var builder = WorkflowBuilder.Create("test");
        builder
            .RequiresSourceControl("sc-reader", new SourceControlCapabilities { RequiredPermissions = [Permission.Read] })
            .RequiresSourceControl("sc-writer", new SourceControlCapabilities { RequiredPermissions = [Permission.Read, Permission.Write] });
        var wb = (WorkflowBuilder)builder;
        wb._directiveTimeout = TimeSpan.FromSeconds(5);

        await wb.RunAsync([], context);

        var schema = ((WorkflowSchemaMessage)bus.Published("workflow.schema")[0]).Schema;
        Assert.That(schema!.Slots, Has.Count.EqualTo(2));
        Assert.That(schema.Slots.Select(s => s.SlotName), Is.EquivalentTo(new[] { "sc-reader", "sc-writer" }));
    }

    [Test]
    public async Task RequiresSourceControl_ReturnsBuilder_ForFluency()
    {
        var builder = WorkflowBuilder.Create("test");
        var returned = builder.RequiresSourceControl("sc", new SourceControlCapabilities
        {
            RequiredPermissions = [Permission.Read]
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

using Auxilia.Messaging;
using Auxilia.Workflows.Capabilities;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class WorkflowBuilderRunRoutingTests
{
    private interface IStubService { }
    [Test]
    public async Task RunAsync_WhenEmitSchemaDirectiveReceived_RoutesToSchemaEmissionPath()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.EmitSchema));
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.RunAsync([], context);

        var schemaMessages = bus.Published("workflow.schema");
        Assert.That(schemaMessages, Has.Count.EqualTo(1));
        Assert.That(schemaMessages[0], Is.InstanceOf<WorkflowSchemaMessage>());
        mockExit.Verify(e => e.Exit(0), Times.Once);
        mockExit.Verify(e => e.Exit(1), Times.Never);
    }

    [Test]
    public async Task RunAsync_WhenRunDirectiveReceived_RoutesToRunPath()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
            {
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.Run));
            }
            else if (topic == "workflow-registration" && message is WorkflowRegistrationRequest req)
            {
                bus.DeliverAsync(req.ResponseTopic,
                    new WorkflowConfigurationResponse(req.WorkflowInstanceId, true, null,
                        new Dictionary<string, EncryptedSlotConfiguration>()));
            }
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow")
            .Requires<IStubService>("slot1", new NoCapabilities());
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.RunAsync([], context);

        var registrations = bus.Published("workflow-registration");
        Assert.That(registrations, Has.Count.EqualTo(1));
        Assert.That(registrations[0], Is.InstanceOf<WorkflowRegistrationRequest>());
    }

    [Test]
    public async Task RunAsync_WhenRunSucceeds_PublishesSuccessStateMessage()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
            {
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.Run));
            }
            else if (topic == "workflow-registration" && message is WorkflowRegistrationRequest req)
            {
                bus.DeliverAsync(req.ResponseTopic,
                    new WorkflowConfigurationResponse(req.WorkflowInstanceId, true, null,
                        new Dictionary<string, EncryptedSlotConfiguration>()));
            }
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.RunAsync([], context);

        var stateMessages = bus.Published("workflow.state");
        Assert.That(stateMessages, Has.Count.EqualTo(1));
        var stateMsg = (WorkflowStateMessage)stateMessages[0];
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(stateMsg.ErrorMessage, Is.Null);
        mockExit.Verify(e => e.Exit(0), Times.Once);
    }

    [Test]
    public async Task RunAsync_WhenRunThrows_PublishesFailedStateMessageAndExits1()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
            {
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.Run));
            }
            else if (topic == "workflow-registration" && message is WorkflowRegistrationRequest req)
            {
                var badSlot = new EncryptedSlotConfiguration("some-provider", "not-valid-base64!!!");
                bus.DeliverAsync(req.ResponseTopic,
                    new WorkflowConfigurationResponse(req.WorkflowInstanceId, true, null,
                        new Dictionary<string, EncryptedSlotConfiguration> { ["bad-slot"] = badSlot }));
            }
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.RunAsync([], context);

        var stateMessages = bus.Published("workflow.state");
        Assert.That(stateMessages, Has.Count.EqualTo(1));
        var stateMsg = (WorkflowStateMessage)stateMessages[0];
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Failed));
        Assert.That(stateMsg.ErrorMessage, Is.Not.Null);
        mockExit.Verify(e => e.Exit(1), Times.Once);
        mockExit.Verify(e => e.Exit(0), Times.Never);
    }

    [TearDown]
    public void TearDown()
    {
        WorkflowBuilder.TestContext = null;
    }

    [Test]
    public async Task Run_WithTestHarnessFlag_AndTestContextSet_UsesTestContext()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.EmitSchema));
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromSeconds(5);
        WorkflowBuilder.TestContext = context;

        await builder.Run(["--test-harness"]);

        var schemaMessages = bus.Published("workflow.schema");
        Assert.That(schemaMessages, Has.Count.EqualTo(1));
        Assert.That(schemaMessages[0], Is.InstanceOf<WorkflowSchemaMessage>());
    }

    [Test]
    public async Task Run_WithTestHarnessFlag_ButTestContextNull_FallsThroughToDefaultPath()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        WorkflowBuilder.TestContext = null;

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        try
        {
            await builder.Run(["--test-harness"]);
        }
        catch
        {
            // Infrastructure exception expected — no RabbitMQ available
        }

        Assert.That(bus.Published("workflow.schema"), Has.Count.EqualTo(0));
        Assert.That(bus.Published("workflow.announcements"), Has.Count.EqualTo(0));
    }

    [Test]
    public async Task Run_WithoutTestHarnessFlag_DoesNotUseTestContext()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        WorkflowBuilder.TestContext = context;

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        try
        {
            await builder.Run([]);
        }
        catch
        {
            // Infrastructure exception expected — no RabbitMQ available
        }

        Assert.That(bus.Published("workflow.schema"), Has.Count.EqualTo(0));
        Assert.That(bus.Published("workflow.announcements"), Has.Count.EqualTo(0));
    }

    // ── Shared fake infrastructure ─────────────────────────────────────────────

    private sealed class FakeWorkflowRunContext(
        IMessageBusClient bus,
        IProcessExitService exitService,
        ILogger? logger = null)
        : IWorkflowRunContext
    {
        public IMessageBusClient MessageBus { get; } = bus;
        public IProcessExitService ExitService { get; } = exitService;
        public ILogger Logger { get; } = logger ?? NullLogger.Instance;
    }

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
            OnPublish?.Invoke(topic, message!);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

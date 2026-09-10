using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class WorkflowBuilderRunStateTests
{
    [Test]
    public async Task Run_WhenRunSucceeds_PublishesSuccessStateMessage()
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

        await builder.Run([], context);

        var stateMessages = bus.Published("workflow.state");
        Assert.That(stateMessages, Has.Count.EqualTo(1));
        var stateMsg = (WorkflowStateMessage)stateMessages[0];
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(stateMsg.ErrorMessage, Is.Null);
        mockExit.Verify(e => e.Exit(0), Times.Once);
    }

    [Test]
    [NonParallelizable] // process environment variables are global state
    public async Task Run_StateMessageCarriesTheWorkflowNameAndTheLaunchInstanceToken()
    {
        // The runner authenticates terminal reports exactly like announcements: the instance
        // token proves the sender, the workflow name binds it to the issued type.
        var original = global::System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceToken);
        global::System.Environment.SetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceToken, "launch-token");
        try
        {
            var bus = new RecordingBus();
            var context = new FakeWorkflowRunContext(bus, Mock.Of<IProcessExitService>());
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

            var stateMsg = (WorkflowStateMessage)bus.Published("workflow.state").Single();
            Assert.Multiple(() =>
            {
                Assert.That(stateMsg.WorkflowName, Is.EqualTo("test-workflow"));
                Assert.That(stateMsg.InstanceToken, Is.EqualTo("launch-token"));
            });
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceToken, original);
        }
    }

    [Test]
    public async Task Run_WhenRunThrows_PublishesFailedStateMessageAndExits1()
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

        await builder.Run([], context);

        var stateMessages = bus.Published("workflow.state");
        Assert.That(stateMessages, Has.Count.EqualTo(1));
        var stateMsg = (WorkflowStateMessage)stateMessages[0];
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Failed));
        Assert.That(stateMsg.ErrorMessage, Is.Not.Null);
        mockExit.Verify(e => e.Exit(1), Times.Once);
        mockExit.Verify(e => e.Exit(0), Times.Never);
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

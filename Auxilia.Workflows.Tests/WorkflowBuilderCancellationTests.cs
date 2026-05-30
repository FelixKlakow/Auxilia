using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class WorkflowBuilderCancellationTests
{
    [Test]
    public async Task Run_WhenRunDirectiveReceived_PassesCancellationTokenToApplicationBody()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);
        var capturedTokenTcs = new TaskCompletionSource<CancellationToken>();

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

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow")
            .WithApplication((_, ct) =>
            {
                capturedTokenTcs.TrySetResult(ct);
                return Task.CompletedTask;
            });
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.Run([], context);

        Assert.That(capturedTokenTcs.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(capturedTokenTcs.Task.Result, Is.Not.EqualTo(CancellationToken.None));
    }

    [Test]
    public async Task Run_WhenCancelWorkflowCommandDelivered_CancelsApplicationToken()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);
        var capturedToken = default(CancellationToken);
        var instanceIdHolder = new Guid[1];

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.Run));
            else if (topic == "workflow-registration" && message is WorkflowRegistrationRequest req)
            {
                instanceIdHolder[0] = req.WorkflowInstanceId;
                bus.DeliverAsync(req.ResponseTopic,
                    new WorkflowConfigurationResponse(req.WorkflowInstanceId, true, null,
                        new Dictionary<string, EncryptedSlotConfiguration>()));
            }
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow")
            .WithApplication(async (_, ct) =>
            {
                capturedToken = ct;
                bus.DeliverAsync($"workflow-cancel-{instanceIdHolder[0]}",
                    new CancelWorkflowCommand(instanceIdHolder[0]));
                await Task.Delay(Timeout.Infinite, ct);
            });
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.Run([], context);

        Assert.That(capturedToken.IsCancellationRequested, Is.True);
        mockExit.Verify(e => e.Exit(0), Times.Once);
        mockExit.Verify(e => e.Exit(1), Times.Never);
    }

    [Test]
    public async Task Run_WhenApplicationCancelled_PublishesWorkflowStateCancelledAndExitsCleanly()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);
        var instanceIdHolder = new Guid[1];

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, WorkflowDirectiveKind.Run));
            else if (topic == "workflow-registration" && message is WorkflowRegistrationRequest req)
            {
                instanceIdHolder[0] = req.WorkflowInstanceId;
                bus.DeliverAsync(req.ResponseTopic,
                    new WorkflowConfigurationResponse(req.WorkflowInstanceId, true, null,
                        new Dictionary<string, EncryptedSlotConfiguration>()));
            }
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow")
            .WithApplication(async (_, ct) =>
            {
                bus.DeliverAsync($"workflow-cancel-{instanceIdHolder[0]}",
                    new CancelWorkflowCommand(instanceIdHolder[0]));
                await Task.Delay(Timeout.Infinite, ct);
            });
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.Run([], context);

        var stateMessages = bus.Published("workflow.state");
        Assert.That(stateMessages, Has.Count.EqualTo(1));
        var stateMsg = (WorkflowStateMessage)stateMessages[0];
        Assert.That(stateMsg.State, Is.EqualTo(WorkflowState.Cancelled));
        Assert.That(stateMsg.ErrorMessage, Is.Null);
        mockExit.Verify(e => e.Exit(0), Times.Once);
        mockExit.Verify(e => e.Exit(1), Times.Never);
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

        public void DeliverAsync<T>(string topic, T message) where T : notnull
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

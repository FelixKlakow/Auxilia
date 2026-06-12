using Auxilia.Messaging;
using Auxilia.Workflows.Capabilities;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class WorkflowBuilderHandshakeTests
{
    private interface IStubService { }

    [Test]
    public async Task Run_PublishesAnnouncementMessageToWorkflowAnnouncementsTopic()
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
                    new WorkflowConfigurationResponse(req.WorkflowInstanceId, false, "rejected",
                        new Dictionary<string, EncryptedSlotConfiguration>()));
            }
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow")
            .Requires<IStubService>("slot1", new NoCapabilities());
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.Run([], context);

        var announcements = bus.Published("workflow.announcements");
        Assert.That(announcements, Has.Count.EqualTo(1));
        var msg = (WorkflowAnnouncementMessage)announcements[0];
        Assert.That(msg.WorkflowName, Is.Not.Empty);
        Assert.That(msg.PublicKey, Is.Not.Empty);
        Assert.That(msg.ResponseTopic, Is.Not.Empty);
    }

    [Test]
    public async Task Run_WhenUnrecognisedDirectiveReceived_LogsErrorAndExits1()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, (WorkflowDirectiveKind)99));
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.Run([], context);

        mockExit.Verify(e => e.Exit(1), Times.Once);
        mockExit.Verify(e => e.Exit(0), Times.Never);
    }

    [Test]
    public async Task Run_WhenRunDirectiveReceived_PublishesWorkflowRegistrationRequest()
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
                    new WorkflowConfigurationResponse(req.WorkflowInstanceId, false, "rejected",
                        new Dictionary<string, EncryptedSlotConfiguration>()));
            }
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow")
            .Requires<IStubService>("slot1", new NoCapabilities());
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.Run([], context);

        var registrations = bus.Published("workflow-registration");
        Assert.That(registrations, Has.Count.EqualTo(1));
        var req = (WorkflowRegistrationRequest)registrations[0];
        Assert.That(req.Manifest, Is.Not.Null);
        Assert.That(req.PublicKey, Is.Not.Empty);
    }

    private sealed record ProgressItem(string Step, int Percent);

    [Test]
    public async Task Run_WithDeclaredViews_RegistrationRequestManifestCarriesViewDescriptors()
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
                    new WorkflowConfigurationResponse(req.WorkflowInstanceId, false, "rejected",
                        new Dictionary<string, EncryptedSlotConfiguration>()));
            }
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow")
            .DeclaresView<ProgressItem>("progress",
                Views.ViewRendering.Stream, Views.ViewLifecycle.LiveAndPersisted);
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.Run([], context);

        var registrations = bus.Published("workflow-registration");
        Assert.That(registrations, Has.Count.EqualTo(1));
        var req = (WorkflowRegistrationRequest)registrations[0];
        Assert.That(req.Manifest.Views, Has.Count.EqualTo(1));
        Assert.That(req.Manifest.Views[0].Name, Is.EqualTo("progress"));
        Assert.That(req.Manifest.Views[0].Rendering, Is.EqualTo(Views.ViewRendering.Stream));
        Assert.That(req.Manifest.Views[0].Lifecycle, Is.EqualTo(Views.ViewLifecycle.LiveAndPersisted));
        Assert.That(req.Manifest.Views[0].ItemSchemaJson, Is.Not.Empty);
    }

    [Test]
    public async Task Run_WhenNoDirectiveReceived_LogsErrorAndExits1()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var loggerMock = new Mock<ILogger>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object, loggerMock.Object);

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromMilliseconds(100);

        await builder.Run([], context);

        mockExit.Verify(e => e.Exit(1), Times.Once);
        mockExit.Verify(e => e.Exit(0), Times.Never);
        loggerMock.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce);
    }

    [Test]
    public async Task Run_WhenPlatformAssignedIdentityPresent_UsesItAndForwardsTokenInBothMessages()
    {
        var assignedId = Guid.NewGuid();
        const string assignedToken = "one-time-token-123";
        System.Environment.SetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceId, assignedId.ToString("D"));
        System.Environment.SetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceToken, assignedToken);
        try
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
                        new WorkflowConfigurationResponse(req.WorkflowInstanceId, false, "rejected",
                            new Dictionary<string, EncryptedSlotConfiguration>()));
            };

            var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow")
                .Requires<IStubService>("slot1", new NoCapabilities());
            builder._directiveTimeout = TimeSpan.FromSeconds(5);

            await builder.Run([], context);

            var ann = (WorkflowAnnouncementMessage)bus.Published("workflow.announcements")[0];
            var req = (WorkflowRegistrationRequest)bus.Published("workflow-registration")[0];
            Assert.Multiple(() =>
            {
                Assert.That(ann.WorkflowInstanceId, Is.EqualTo(assignedId));
                Assert.That(ann.InstanceToken, Is.EqualTo(assignedToken));
                Assert.That(ann.ResponseTopic, Is.EqualTo(Auxilia.Workflows.Messaging.WorkflowQueues.ResponseQueueFor(assignedId)));
                Assert.That(req.WorkflowInstanceId, Is.EqualTo(assignedId));
                Assert.That(req.InstanceToken, Is.EqualTo(assignedToken));
            });
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceId, null);
            System.Environment.SetEnvironmentVariable(WorkflowEnvironmentVariables.InstanceToken, null);
        }
    }

    [Test]
    public async Task Run_WithoutPlatformAssignedIdentity_AnnouncesWithoutToken()
    {
        var bus = new RecordingBus();
        var mockExit = new Mock<IProcessExitService>();
        var context = new FakeWorkflowRunContext(bus, mockExit.Object);

        bus.OnPublish = (topic, message) =>
        {
            if (topic == "workflow.announcements" && message is WorkflowAnnouncementMessage ann)
                bus.DeliverAsync(ann.ResponseTopic,
                    new WorkflowDirective(ann.WorkflowInstanceId, (WorkflowDirectiveKind)99));
        };

        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        builder._directiveTimeout = TimeSpan.FromSeconds(5);

        await builder.Run([], context);

        var ann = (WorkflowAnnouncementMessage)bus.Published("workflow.announcements")[0];
        Assert.That(ann.InstanceToken, Is.Null);
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

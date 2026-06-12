using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class ResourceProxyHandlerTests
{
    private CapturingBus _bus = null!;
    private WorkflowInstanceTokenRegistry _tokenRegistry = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new CapturingBus();
        _tokenRegistry = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System);
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
    }

    [TearDown]
    public void TearDown() => _auditRecords.Dispose();

    private async Task<ResourceProxyHandler> StartHandlerAsync(
        bool requireInstanceToken = true, params IResourceConnector[] connectors)
    {
        var handler = new ResourceProxyHandler(
            _bus,
            connectors,
            _tokenRegistry,
            new AuditLog(_auditRecords, TimeProvider.System),
            Options.Create(new WorkflowDispatcherSettings { RequireInstanceToken = requireInstanceToken }),
            NullLogger<ResourceProxyHandler>.Instance);
        await handler.StartAsync(CancellationToken.None);
        return handler;
    }

    private async Task<List<AuditRecord>> AuditEntriesAsync(string action)
        => (await _auditRecords.ReadAsync()).Where(r => r.Action == action).ToList();

    private static ResourceRequest Request(
        Guid instanceId, string? token, string resourceName = "task-source", string operation = "create-comment")
        => new(instanceId, resourceName, operation, """{"text":"hi"}""", Guid.NewGuid(), token);

    [Test]
    public async Task HandleAsync_ValidTokenAndKnownConnector_RespondsSuccessOnCanonicalQueue()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await StartHandlerAsync(connectors: new StubConnector("task-source", _ => """{"ok":true}"""));

        var request = Request(issued.WorkflowInstanceId, issued.Token);
        await _bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var (topic, msg) = _bus.Published[0];
        Assert.That(topic, Is.EqualTo(WorkflowQueues.ResourceResponseQueueFor(issued.WorkflowInstanceId)));
        var response = (ResourceResponse)msg;
        Assert.Multiple(() =>
        {
            Assert.That(response.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(response.Success, Is.True);
            Assert.That(response.ErrorMessage, Is.Null);
            Assert.That(response.ResultJson, Is.EqualTo("""{"ok":true}"""));
        });
    }

    [Test]
    public async Task HandleAsync_SuccessfulCall_AuditsResourceCall()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await StartHandlerAsync(connectors: new StubConnector("task-source", _ => "{}"));

        await _bus.InvokeAsync(Request(issued.WorkflowInstanceId, issued.Token), CancellationToken.None);

        var entries = await AuditEntriesAsync("workflow.resource-call");
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Subject, Is.EqualTo(issued.WorkflowInstanceId.ToString()));
            Assert.That(entries[0].Outcome, Is.EqualTo("task-source:create-comment"));
        });
    }

    [Test]
    public async Task HandleAsync_UnknownResource_PublishesFailureResponse()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await StartHandlerAsync(connectors: new StubConnector("task-source", _ => "{}"));

        var request = Request(issued.WorkflowInstanceId, issued.Token, resourceName: "ghost-resource");
        await _bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var response = (ResourceResponse)_bus.Published[0].Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(response.Success, Is.False);
            Assert.That(response.ErrorMessage, Does.Contain("ghost-resource"));
            Assert.That(response.ResultJson, Is.Null);
        });
    }

    [Test]
    public async Task HandleAsync_InvalidToken_PublishesNothingAndAuditsRejection()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await StartHandlerAsync(connectors: new StubConnector("task-source", _ => "{}"));

        await _bus.InvokeAsync(
            Request(issued.WorkflowInstanceId, "not-the-issued-token"), CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty,
            "An invalid token must be rejected silently — no response on any queue.");
        var rejections = await AuditEntriesAsync("workflow.resource.rejected");
        Assert.That(rejections, Has.Count.EqualTo(1));
        Assert.That(rejections[0].Outcome, Is.EqualTo("invalid-instance-token"));
    }

    [Test]
    public async Task HandleAsync_ConnectorThrows_PublishesFailureResponse()
    {
        var issued = _tokenRegistry.Issue("TestWorkflow");
        await StartHandlerAsync(connectors: new StubConnector(
            "task-source", _ => throw new InvalidOperationException("backend unavailable")));

        var request = Request(issued.WorkflowInstanceId, issued.Token);
        await _bus.InvokeAsync(request, CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        var response = (ResourceResponse)_bus.Published[0].Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(response.Success, Is.False);
            Assert.That(response.ErrorMessage, Does.Contain("backend unavailable"));
        });
    }

    [Test]
    public async Task HandleAsync_TokenNotRequired_ExecutesWithoutToken()
    {
        await StartHandlerAsync(requireInstanceToken: false,
            connectors: new StubConnector("task-source", _ => "{}"));

        await _bus.InvokeAsync(Request(Guid.NewGuid(), token: null), CancellationToken.None);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        Assert.That(((ResourceResponse)_bus.Published[0].Message).Success, Is.True);
    }

    [Test]
    public async Task StartAsync_DeclaresAndSubscribesResourceProxyQueue()
    {
        await StartHandlerAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_bus.DeclaredQueues, Does.Contain("workflow-resource-proxy"));
            Assert.That(_bus.SubscribedQueues, Does.Contain("workflow-resource-proxy"));
        });
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class StubConnector(string resourceName, Func<string, string> execute) : IResourceConnector
    {
        public string ResourceName => resourceName;

        public Task<string> ExecuteAsync(string operation, string payloadJson, CancellationToken ct)
            => Task.FromResult(execute(operation));
    }

    // A fake that captures the subscription callback so tests can invoke it directly.
    private sealed class CapturingBus : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];
        public List<string> DeclaredQueues { get; } = [];
        public List<string> SubscribedQueues { get; } = [];
        private Func<ResourceRequest, CancellationToken, Task>? _handler;

        public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
        {
            DeclaredQueues.Add(queueName);
            return Task.CompletedTask;
        }

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

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            string queueName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            SubscribedQueues.Add(queueName);
            if (handler is Func<ResourceRequest, CancellationToken, Task> typedHandler)
                _handler = typedHandler;
            return Task.FromResult<IAsyncDisposable>(new NullDisposable());
        }

        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
            string exchangeName,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(new NullDisposable());

        public Task InvokeAsync(ResourceRequest request, CancellationToken cancellationToken)
            => _handler!(request, cancellationToken);

        private sealed class NullDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

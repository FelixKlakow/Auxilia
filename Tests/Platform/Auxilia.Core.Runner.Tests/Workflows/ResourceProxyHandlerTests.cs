using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Tests.Workflows;

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
        _bus.AfterInvoke = handler.DrainAsync;
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
    public async Task ASlowCall_DoesNotHoldUpAnotherRunsCall()
    {
        var slow = _tokenRegistry.Issue("TestWorkflow");
        var fast = _tokenRegistry.Issue("TestWorkflow");
        var release = new TaskCompletionSource();
        var handler = await StartHandlerAsync(connectors:
        [
            new BlockingConnector("run-checkpoints", release.Task),
            new StubConnector("task-source", _ => """{"ok":true}"""),
        ]);

        await _bus.AdmitAsync(Request(slow.WorkflowInstanceId, slow.Token, resourceName: "run-checkpoints", operation: "save"));
        await _bus.AdmitAsync(Request(fast.WorkflowInstanceId, fast.Token));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_bus.PublishedSnapshot().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        var answeredFirst = _bus.PublishedSnapshot().Select(p => p.Topic).ToList();
        release.SetResult();
        await handler.DrainAsync();

        Assert.Multiple(() =>
        {
            Assert.That(answeredFirst, Is.EqualTo(new[] { WorkflowQueues.ResourceResponseQueueFor(fast.WorkflowInstanceId) }),
                "the other run's call is answered while the slow one still runs");
            Assert.That(_bus.PublishedSnapshot(), Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task ARun_HasAtMostTwoCallsInFlight_AFurtherOneIsRefusedAtOnce()
    {
        var run = _tokenRegistry.Issue("TestWorkflow");
        var release = new TaskCompletionSource();
        var handler = await StartHandlerAsync(connectors: new BlockingConnector("run-checkpoints", release.Task));
        var requests = Enumerable.Range(0, 3)
            .Select(_ => Request(run.WorkflowInstanceId, run.Token, resourceName: "run-checkpoints", operation: "save"))
            .ToList();

        foreach (var request in requests)
            await _bus.AdmitAsync(request);
        var refusedAtOnce = _bus.PublishedSnapshot().Select(p => (ResourceResponse)p.Message).ToList();
        release.SetResult();
        await handler.DrainAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refusedAtOnce.Select(r => r.RequestId), Is.EqualTo(new[] { requests[2].RequestId }),
                "the third is answered while the first two still run");
            Assert.That(refusedAtOnce.Single().Success, Is.False);
            Assert.That(_bus.PublishedSnapshot().Select(p => (ResourceResponse)p.Message).Count(r => r.Success), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ACallPastItsDeadline_IsAnsweredAsFailed_AndFreesItsPlace()
    {
        var run = _tokenRegistry.Issue("TestWorkflow");
        var handler = await StartHandlerAsync(connectors: new BlockingConnector("run-checkpoints", new TaskCompletionSource().Task));
        handler.CallDeadline = TimeSpan.FromMilliseconds(200);

        await _bus.InvokeAsync(
            Request(run.WorkflowInstanceId, run.Token, resourceName: "run-checkpoints", operation: "save"), CancellationToken.None);

        var response = (ResourceResponse)_bus.PublishedSnapshot().Single().Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.ErrorMessage, Does.Contain("did not finish"));
        });
    }

    [Test]
    public async Task ACallThatFindsNoCapacityInTime_IsRefused_BeforeTheWorkflowStopsWaiting()
    {
        var release = new TaskCompletionSource();
        // Calls that hold their slot past any deadline: the late call's wait alone decides.
        var handler = await StartHandlerAsync(connectors: new StubbornConnector("run-checkpoints", release.Task));
        handler.CallDeadline = TimeSpan.FromMilliseconds(300);
        // Four runs fill every slot with two slow calls each.
        for (var i = 0; i < ResourceProxyHandler.MaxConcurrentCalls / ResourceProxyHandler.MaxCallsPerInstance; i++)
        {
            var busy = _tokenRegistry.Issue("TestWorkflow");
            for (var call = 0; call < ResourceProxyHandler.MaxCallsPerInstance; call++)
                await _bus.AdmitAsync(Request(busy.WorkflowInstanceId, busy.Token, resourceName: "run-checkpoints", operation: "save"));
        }
        var late = _tokenRegistry.Issue("TestWorkflow");
        var lateRequest = Request(late.WorkflowInstanceId, late.Token, resourceName: "run-checkpoints", operation: "save");

        await _bus.AdmitAsync(lateRequest);
        var refused = _bus.PublishedSnapshot().Select(p => (ResourceResponse)p.Message)
            .SingleOrDefault(r => r.RequestId == lateRequest.RequestId);
        release.SetResult();
        await handler.DrainAsync();

        Assert.That(refused, Is.Not.Null);
        Assert.That(refused!.ErrorMessage, Does.Contain("too busy"));
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

    private sealed class StubbornConnector(string resourceName, Task release) : IResourceConnector
    {
        public string ResourceName => resourceName;

        public async Task<string> ExecuteAsync(Guid instanceId, string operation, string payloadJson, CancellationToken ct)
        {
            await release;
            return "{}";
        }
    }

    private sealed class BlockingConnector(string resourceName, Task release) : IResourceConnector
    {
        public string ResourceName => resourceName;

        public async Task<string> ExecuteAsync(Guid instanceId, string operation, string payloadJson, CancellationToken ct)
        {
            await release.WaitAsync(ct);
            return "{}";
        }
    }

    private sealed class StubConnector(string resourceName, Func<string, string> execute) : IResourceConnector
    {
        public string ResourceName => resourceName;

        public Task<string> ExecuteAsync(Guid instanceId, string operation, string payloadJson, CancellationToken ct)
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
            lock (Published)
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

        /// <summary>Awaited after each delivery — the handler answers calls off the subscription.</summary>
        public Func<Task>? AfterInvoke { get; set; }

        public async Task InvokeAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            await _handler!(request, cancellationToken);
            if (AfterInvoke is not null)
                await AfterInvoke();
        }

        /// <summary>One delivery, without waiting for its answer.</summary>
        public Task AdmitAsync(ResourceRequest request) => _handler!(request, CancellationToken.None);

        public List<(string Topic, object Message)> PublishedSnapshot()
        {
            lock (Published)
                return [.. Published];
        }

        private sealed class NullDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

using Auxilia.BackendService.Dashboard;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Moq;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class RunCancelServiceTests
{
    private sealed class RecordingBus : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];
        public Task DeclareQueueAsync(string q, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclareExchangeAsync(string e, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishAsync<T>(string topic, T message, CancellationToken ct = default)
        {
            Published.Add((topic, message!));
            return Task.CompletedTask;
        }
        public Task PublishToExchangeAsync<T>(string e, T m, CancellationToken ct = default) => PublishAsync(e, m, ct);
        public Task<IAsyncDisposable> SubscribeAsync<T>(string q, Func<T, CancellationToken, Task> h, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());
        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(string e, Func<T, CancellationToken, Task> h, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());
        private sealed class Noop : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private RecordingBus _bus = null!;
    private IDataAccess<WorkflowInstanceRecord> _instances = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private Mock<IPolicyEngine> _policyEngine = null!;
    private RunCancelService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new RecordingBus();
        _instances = new InMemoryDataAccess<WorkflowInstanceRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _policyEngine = new Mock<IPolicyEngine>(MockBehavior.Strict);
        _sut = new RunCancelService(
            _instances, _policyEngine.Object, _bus, new AuditLog(_audit, TimeProvider.System));
    }

    [TearDown]
    public void TearDown()
    {
        (_instances as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private async Task<Guid> SeedRunAsync(string state)
    {
        var instanceId = Guid.NewGuid();
        await _instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            WorkflowType = "pull-request-code-review",
            State = state,
            CreatedUtc = DateTimeOffset.UtcNow
        });
        return instanceId;
    }

    private void AllowPolicy(Guid actor)
        => _policyEngine
            .Setup(p => p.EvaluateAsync(
                It.Is<PolicyContext>(c => c.PrincipalId == actor && c.Action == PermissionActions.WorkflowCancel),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PolicyDecision.Allow("role"));

    [Test]
    public async Task Cancel_OfRunningRun_PublishesTheCancelCommand_AndAudits()
    {
        var actor = Guid.NewGuid();
        var instanceId = await SeedRunAsync("Running");
        AllowPolicy(actor);

        await _sut.RequestCancelAsync(actor, instanceId);

        var published = _bus.Published.Single();
        Assert.Multiple(async () =>
        {
            Assert.That(published.Topic, Is.EqualTo(RunCancelService.CancelQueueName));
            Assert.That(((CancelWorkflowCommand)published.Message).WorkflowInstanceId, Is.EqualTo(instanceId));
            var record = (await _audit.ReadAsync()).Single(a => a.Action == "workflow.cancel-requested");
            Assert.That(record.Actor, Is.EqualTo(actor.ToString("D")));
            Assert.That(record.Subject, Is.EqualTo(instanceId.ToString()));
        });
    }

    [Test]
    public async Task Cancel_OfQueuedRun_IsAccepted()
    {
        var actor = Guid.NewGuid();
        var instanceId = await SeedRunAsync("Queued");
        AllowPolicy(actor);

        await _sut.RequestCancelAsync(actor, instanceId);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
    }

    [TestCase("Success")]
    [TestCase("Failed")]
    [TestCase("Cancelled")]
    [TestCase("PreFlightFailed")]
    public async Task Cancel_OfTerminalRun_IsRejected(string state)
    {
        var instanceId = await SeedRunAsync(state);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RequestCancelAsync(Guid.NewGuid(), instanceId));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("already finished"));
            Assert.That(_bus.Published, Is.Empty);
        });
    }

    [Test]
    public void Cancel_OfUnknownRun_IsRejected()
    {
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RequestCancelAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.That(_bus.Published, Is.Empty);
    }

    [Test]
    public async Task Cancel_DeniedByPolicy_PublishesNothing()
    {
        var instanceId = await SeedRunAsync("Running");
        _policyEngine
            .Setup(p => p.EvaluateAsync(It.IsAny<PolicyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PolicyDecision.Deny("no role grants workflow.cancel"));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RequestCancelAsync(Guid.NewGuid(), instanceId));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("workflow.cancel denied"));
            Assert.That(_bus.Published, Is.Empty);
        });
    }
}

using System.Text.Json;
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
public class WorkflowRerunServiceTests
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
    private WorkflowRerunService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new RecordingBus();
        _instances = new InMemoryDataAccess<WorkflowInstanceRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _policyEngine = new Mock<IPolicyEngine>(MockBehavior.Strict);
        _sut = new WorkflowRerunService(
            _instances, _policyEngine.Object, _bus,
            new AuditLog(_audit, TimeProvider.System),
            new DashboardSettings());
    }

    [TearDown]
    public void TearDown()
    {
        (_instances as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private static RunWorkflowCommand OriginalCommand(Guid? configurationId = null)
        => new(Guid.NewGuid(), "pull-request-code-review", "docker://review:1",
            new Dictionary<string, string> { ["WorkItemId"] = "mail-42" },
            RequestedBy: Guid.NewGuid(), WorkflowConfigurationId: configurationId);

    private async Task<Guid> SeedRunAsync(
        RunWorkflowCommand? command = null, string state = "Success", bool withCommand = true)
    {
        var instanceId = Guid.NewGuid();
        await _instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            WorkflowType = "pull-request-code-review",
            State = state,
            CreatedUtc = DateTimeOffset.UtcNow,
            DispatchCommandJson = withCommand ? JsonSerializer.Serialize(command ?? OriginalCommand()) : null
        });
        return instanceId;
    }

    private void AllowPolicy(Guid actor)
        => _policyEngine
            .Setup(p => p.EvaluateAsync(
                It.Is<PolicyContext>(c => c.PrincipalId == actor && c.Action == PermissionActions.WorkflowTrigger),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PolicyDecision.Allow("role"));

    [Test]
    public async Task Rerun_RepublishesTheOriginalCommand_WithFreshIdRequesterAndRerunContext()
    {
        var actor = Guid.NewGuid();
        var configurationId = Guid.NewGuid();
        var original = OriginalCommand(configurationId);
        var instanceId = await SeedRunAsync(original);
        AllowPolicy(actor);

        var commandId = await _sut.RerunAsync(actor, instanceId);

        var published = _bus.Published.Single();
        var command = (RunWorkflowCommand)published.Message;
        Assert.Multiple(() =>
        {
            Assert.That(published.Topic, Is.EqualTo(new DashboardSettings().CommandQueueName));
            Assert.That(command.CommandId, Is.EqualTo(commandId));
            Assert.That(command.CommandId, Is.Not.EqualTo(original.CommandId), "a rerun gets a fresh command ID");
            Assert.That(command.RequestedBy, Is.EqualTo(actor), "the rerunning principal becomes the requester");
            Assert.That(command.Context["RERUN_OF"], Is.EqualTo(instanceId.ToString("D")));
            Assert.That(command.Context["WorkItemId"], Is.EqualTo("mail-42"), "the original context is preserved");
            Assert.That(command.WorkflowType, Is.EqualTo(original.WorkflowType));
            Assert.That(command.WorkflowPackageUri, Is.EqualTo(original.WorkflowPackageUri));
            Assert.That(command.WorkflowConfigurationId, Is.EqualTo(configurationId));
        });
    }

    [Test]
    public async Task Rerun_IsAudited_WithPredecessorInstanceId()
    {
        var actor = Guid.NewGuid();
        var instanceId = await SeedRunAsync();
        AllowPolicy(actor);

        await _sut.RerunAsync(actor, instanceId);

        var record = (await _audit.ReadAsync()).Single(a => a.Action == "workflow.rerun");
        Assert.Multiple(() =>
        {
            Assert.That(record.Actor, Is.EqualTo(actor.ToString("D")));
            Assert.That(record.Subject, Is.EqualTo(instanceId.ToString()));
            Assert.That(record.DetailJson, Does.Contain(instanceId.ToString("D")));
        });
    }

    [Test]
    public async Task Rerun_OfRunningRun_IsRejected()
    {
        var instanceId = await SeedRunAsync(state: "Running");

        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RerunAsync(Guid.NewGuid(), instanceId));
        Assert.That(_bus.Published, Is.Empty);
    }

    [Test]
    public async Task Rerun_WithoutStoredDispatchCommand_IsRejected()
    {
        var instanceId = await SeedRunAsync(withCommand: false);

        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RerunAsync(Guid.NewGuid(), instanceId));
    }

    [Test]
    public async Task Rerun_DeniedByPolicy_PublishesNothing()
    {
        var actor = Guid.NewGuid();
        var instanceId = await SeedRunAsync();
        _policyEngine
            .Setup(p => p.EvaluateAsync(It.IsAny<PolicyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PolicyDecision.Deny("no role grants workflow.trigger"));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RerunAsync(actor, instanceId));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("workflow.trigger denied"));
            Assert.That(_bus.Published, Is.Empty);
        });
    }

    [Test]
    public async Task RerunOf_SurfacesThePredecessor_FromTheDispatchContext()
    {
        var predecessor = Guid.NewGuid();
        var command = OriginalCommand() with
        {
            Context = new Dictionary<string, string> { ["RERUN_OF"] = predecessor.ToString("D") }
        };
        var instanceId = await SeedRunAsync(command);

        var run = await _instances.ReadAsync(instanceId);

        Assert.That(WorkflowRerunService.RerunOf(run!), Is.EqualTo(predecessor));
    }

    [Test]
    public void BuildRerunCommand_OverwritesAnInheritedRerunMarker()
    {
        var firstRun = Guid.NewGuid();
        var secondRun = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var commandOfSecond = OriginalCommand() with
        {
            Context = new Dictionary<string, string> { ["RERUN_OF"] = firstRun.ToString("D") }
        };

        var third = WorkflowRerunService.BuildRerunCommand(commandOfSecond, secondRun, actor);

        Assert.That(third.Context["RERUN_OF"], Is.EqualTo(secondRun.ToString("D")),
            "a rerun of a rerun points at its immediate predecessor");
    }
}

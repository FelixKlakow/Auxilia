using Auxilia.BackendService.PlatformHost;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class HeartbeatMonitorTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingBus : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];

        public Task DeclareQueueAsync(string queueName, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclareExchangeAsync(string exchangeName, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishAsync<T>(string topic, T message, CancellationToken ct = default)
        {
            Published.Add((topic, message!));
            return Task.CompletedTask;
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken ct = default)
            => PublishAsync(exchangeName, message, ct);

        public Task<IAsyncDisposable> SubscribeAsync<T>(string queueName, Func<T, CancellationToken, Task> handler, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());
        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(string exchangeName, Func<T, CancellationToken, Task> handler, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());

        private sealed class Noop : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private ManualTimeProvider _time = null!;
    private RecordingBus _bus = null!;
    private IDataAccess<ServiceHeartbeatRecord> _heartbeats = null!;
    private IDataAccess<WorkflowInstanceRecord> _instances = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private PlatformHostSettings _settings = null!;
    private HeartbeatMonitor _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _bus = new RecordingBus();
        _heartbeats = new InMemoryDataAccess<ServiceHeartbeatRecord>();
        _instances = new InMemoryDataAccess<WorkflowInstanceRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _settings = new PlatformHostSettings { HeartbeatTimeoutSeconds = 30 };
        _sut = new HeartbeatMonitor(
            _heartbeats, _instances, _bus,
            new WorkflowStatusPublisher(_bus, _time),
            new AuditLog(_audit, _time),
            _time, Options.Create(_settings),
            NullLogger<HeartbeatMonitor>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        (_heartbeats as IDisposable)?.Dispose();
        (_instances as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private async Task<(Guid ServiceId, WorkflowInstanceRecord Run)> SeedDeadServiceWithRunAsync(
        string state = "Running", IReadOnlyDictionary<string, string>? commandContext = null)
    {
        var serviceId = Guid.NewGuid();
        await _heartbeats.SaveAsync(new ServiceHeartbeatRecord
        {
            Id = serviceId,
            ServiceName = "Auxilia.SteeringInstance",
            LastBeatUtc = _time.Now - TimeSpan.FromSeconds(60) // stale
        });

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf-type", "docker://wf:test",
            commandContext ?? new Dictionary<string, string>());
        var run = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "wf-type",
            State = state,
            CreatedUtc = _time.Now - TimeSpan.FromMinutes(5),
            OwnerServiceId = serviceId,
            DispatchCommandJson = JsonSerializer.Serialize(command)
        };
        await _instances.SaveAsync(run);
        return (serviceId, run);
    }

    [Test]
    public async Task StaleHeartbeat_OrphanedRun_IsFailedCancelledAuditedAndRedispatched()
    {
        var (_, run) = await SeedDeadServiceWithRunAsync();

        await _sut.ScanAsync(CancellationToken.None);

        var updated = await _instances.ReadAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updated!.State, Is.EqualTo("Failed"));
            Assert.That(updated.ErrorMessage, Is.EqualTo("steering-instance-lost"));
            Assert.That(_bus.Published.Any(p =>
                p.Topic == $"workflow-cancel-{run.Id}" && p.Message is CancelWorkflowCommand), Is.True,
                "Cancel must be published to the run's cancel queue.");
            Assert.That(_bus.Published.Any(p =>
                p.Message is WorkflowStatusEvent e && e.State == "Failed" && e.WorkflowInstanceId == run.Id), Is.True,
                "The failover must be surfaced as a status event.");
        });

        var redispatch = _bus.Published
            .Where(p => p.Topic == _settings.CommandQueueName)
            .Select(p => p.Message).OfType<RunWorkflowCommand>().SingleOrDefault();
        Assert.That(redispatch, Is.Not.Null, "The run must be re-dispatched.");
        Assert.That(redispatch!.Context.ContainsKey(HeartbeatMonitor.FailoverContextKey), Is.True);

        var auditQuery = await _audit.ReadAsync();
        Assert.That(auditQuery.Any(a => a.Action == "workflow.failover"), Is.True);
    }

    [Test]
    public async Task FreshHeartbeat_NothingHappens()
    {
        var (serviceId, _) = await SeedDeadServiceWithRunAsync();
        await _heartbeats.SaveAsync(new ServiceHeartbeatRecord
        {
            Id = serviceId,
            ServiceName = "Auxilia.SteeringInstance",
            LastBeatUtc = _time.Now // fresh again
        });

        await _sut.ScanAsync(CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty);
    }

    [Test]
    public async Task TerminalRun_IsNotFailedOver()
    {
        var (_, run) = await SeedDeadServiceWithRunAsync(state: "Success");

        await _sut.ScanAsync(CancellationToken.None);

        var updated = await _instances.ReadAsync(run.Id);
        Assert.That(updated!.State, Is.EqualTo("Success"));
        Assert.That(_bus.Published.Any(p => p.Topic == $"workflow-cancel-{run.Id}"), Is.False);
    }

    [Test]
    public async Task RunThatWasAlreadyARedispatch_IsNotRedispatchedAgain()
    {
        await SeedDeadServiceWithRunAsync(commandContext: new Dictionary<string, string>
        {
            [HeartbeatMonitor.FailoverContextKey] = Guid.NewGuid().ToString("D")
        });

        await _sut.ScanAsync(CancellationToken.None);

        Assert.That(_bus.Published.Where(p => p.Topic == _settings.CommandQueueName), Is.Empty,
            "A failover re-dispatch must not cascade into further re-dispatches.");
    }

    [Test]
    public async Task RedispatchDisabled_RunIsFailedButNotRedispatched()
    {
        _settings.RedispatchOnFailover = false;
        var (_, run) = await SeedDeadServiceWithRunAsync();

        await _sut.ScanAsync(CancellationToken.None);

        var updated = await _instances.ReadAsync(run.Id);
        Assert.That(updated!.State, Is.EqualTo("Failed"));
        Assert.That(_bus.Published.Where(p => p.Topic == _settings.CommandQueueName), Is.Empty);
    }

    [Test]
    public async Task StaleHeartbeat_IsRemovedAfterFailover_SoScanIsIdempotent()
    {
        var (serviceId, _) = await SeedDeadServiceWithRunAsync();

        await _sut.ScanAsync(CancellationToken.None);
        var publishedAfterFirst = _bus.Published.Count;
        await _sut.ScanAsync(CancellationToken.None);

        Assert.That(await _heartbeats.ReadAsync(serviceId), Is.Null);
        Assert.That(_bus.Published, Has.Count.EqualTo(publishedAfterFirst),
            "A second scan must not repeat the failover.");
    }
}

using System.Text.Json;
using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Unit tests for the Core.Api failover monitor. Liveness is fed through the in-memory
/// <see cref="RunnerLivenessTracker"/>; the failover sweep is driven directly via
/// <c>ScanOnceAsync</c>. Only the Core's own <see cref="CoreRunRecord"/> store is consulted —
/// never a runner database.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class FailoverMonitorTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private ManualTimeProvider _time = null!;
    private FakeMessageBusClient _bus = null!;
    private RunnerLivenessTracker _liveness = null!;
    private IDataAccess<CoreRunRecord> _runs = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private CoreApiSettings _settings = null!;
    private FailoverMonitor _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _bus = new FakeMessageBusClient();
        _liveness = new RunnerLivenessTracker();
        _runs = new InMemoryDataAccess<CoreRunRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _settings = new CoreApiSettings { HeartbeatTimeoutSeconds = 30, FailoverScanIntervalSeconds = 3600 };
        _sut = new FailoverMonitor(
            _bus, _liveness, _runs,
            new WorkflowStatusPublisher(_bus, _time),
            new AuditLog(_audit, _time),
            _time, Options.Create(_settings),
            NullLogger<FailoverMonitor>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        (_runs as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private async Task<(Guid ServiceId, CoreRunRecord Run)> SeedDeadRunnerWithRunAsync(
        string state = "Running", IReadOnlyDictionary<string, string>? commandContext = null)
    {
        var serviceId = Guid.NewGuid();
        // Last beat is stale relative to the 30s timeout at 'Now'.
        _liveness.Record(serviceId, _time.Now - TimeSpan.FromSeconds(60));

        var runId = Guid.NewGuid();
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf-type", "docker://wf:test",
            commandContext ?? new Dictionary<string, string>());
        var run = new CoreRunRecord
        {
            Id = runId,
            WorkflowType = "wf-type",
            State = state,
            CreatedUtc = _time.Now - TimeSpan.FromMinutes(5),
            UpdatedUtc = _time.Now - TimeSpan.FromMinutes(5),
            OwnerServiceId = serviceId,
            DispatchCommandJson = JsonSerializer.Serialize(command)
        };
        await _runs.SaveAsync(run);
        return (serviceId, run);
    }

    [Test]
    public async Task StaleRunner_OrphanedRun_IsFailedCancelledAuditedAndRedispatched()
    {
        var (_, run) = await SeedDeadRunnerWithRunAsync();

        await _sut.ScanOnceAsync(CancellationToken.None);

        var updated = await _runs.ReadAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updated!.State, Is.EqualTo("Failed"));
            Assert.That(updated.ErrorMessage, Is.EqualTo("steering-instance-lost"));
            Assert.That(_bus.PublishedMessages.Any(p =>
                p.Topic == $"workflow-cancel-{run.Id}" && p.Message is CancelWorkflowCommand), Is.True,
                "Cancel must be published to the run's cancel queue.");
            Assert.That(_bus.PublishedMessages.Any(p =>
                p.Message is WorkflowStatusEvent e && e.State == "Failed" && e.WorkflowInstanceId == run.Id), Is.True,
                "The failover must be surfaced as a status event.");
        });

        var redispatch = _bus.PublishedMessages
            .Where(p => p.Topic == _settings.RunCommandQueue)
            .Select(p => p.Message).OfType<RunWorkflowCommand>().SingleOrDefault();
        Assert.That(redispatch, Is.Not.Null, "The run must be re-dispatched.");
        Assert.That(redispatch!.Context.ContainsKey(FailoverMonitor.FailoverContextKey), Is.True);

        var auditQuery = await _audit.ReadAsync();
        Assert.That(auditQuery.Any(a => a.Action == "workflow.failover"), Is.True);
        Assert.That(auditQuery.Any(a => a.Action == "workflow.redispatched"), Is.True);
    }

    [Test]
    public async Task LiveRunner_RunsAreUntouched()
    {
        var (serviceId, run) = await SeedDeadRunnerWithRunAsync();
        // A fresh beat at 'Now' keeps the runner alive.
        _liveness.Record(serviceId, _time.Now);

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.That((await _runs.ReadAsync(run.Id))!.State, Is.EqualTo("Running"));
        Assert.That(_bus.PublishedMessages, Is.Empty);
    }

    [Test]
    public async Task TerminalRun_IsNotFailedOver()
    {
        var (_, run) = await SeedDeadRunnerWithRunAsync(state: "Success");

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.That((await _runs.ReadAsync(run.Id))!.State, Is.EqualTo("Success"));
        Assert.That(_bus.PublishedMessages.Any(p => p.Topic == $"workflow-cancel-{run.Id}"), Is.False);
    }

    [Test]
    public async Task RunThatWasAlreadyARedispatch_IsNotRedispatchedAgain()
    {
        var (_, run) = await SeedDeadRunnerWithRunAsync(commandContext: new Dictionary<string, string>
        {
            [FailoverMonitor.FailoverContextKey] = Guid.NewGuid().ToString("D")
        });

        await _sut.ScanOnceAsync(CancellationToken.None);

        // Still failed + cancelled, but the guard prevents a redispatch loop.
        Assert.That((await _runs.ReadAsync(run.Id))!.State, Is.EqualTo("Failed"));
        Assert.That(_bus.PublishedMessages.Where(p => p.Topic == _settings.RunCommandQueue), Is.Empty,
            "A failover re-dispatch must not cascade into further re-dispatches.");
    }

    [Test]
    public async Task RedispatchDisabled_RunIsFailedButNotRedispatched()
    {
        _settings.RedispatchOnFailover = false;
        var (_, run) = await SeedDeadRunnerWithRunAsync();

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.That((await _runs.ReadAsync(run.Id))!.State, Is.EqualTo("Failed"));
        Assert.That(_bus.PublishedMessages.Where(p => p.Topic == _settings.RunCommandQueue), Is.Empty);
    }

    [Test]
    public async Task DeadRunner_IsForgottenAfterFailover_SoScanIsIdempotent()
    {
        await SeedDeadRunnerWithRunAsync();

        await _sut.ScanOnceAsync(CancellationToken.None);
        var publishedAfterFirst = _bus.PublishedMessages.Count;
        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.That(_bus.PublishedMessages, Has.Count.EqualTo(publishedAfterFirst),
            "A second scan must not repeat the failover.");
    }
}

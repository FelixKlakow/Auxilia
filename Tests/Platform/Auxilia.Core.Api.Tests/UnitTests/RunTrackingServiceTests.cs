using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Unit tests for the run-tracking mirror: rekey-on-claim (the dispatch-authored command-keyed
/// record merges onto the runner's instance id) and the terminal sink (a terminal record is never
/// resurrected by late events from stale commands or re-adopted runners).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunTrackingServiceTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 4, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private ManualTimeProvider _time = null!;
    private FakeMessageBusClient _bus = null!;
    private IDataAccess<CoreRunRecord> _runs = null!;
    private IDataAccess<CoreRunResolutionRecord> _resolutions = null!;
    private WorkflowStatusPublisher _publisher = null!;
    private RunTrackingService _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _time = new ManualTimeProvider();
        _bus = new FakeMessageBusClient();
        _runs = new InMemoryDataAccess<CoreRunRecord>();
        _resolutions = new InMemoryDataAccess<CoreRunResolutionRecord>();
        _publisher = new WorkflowStatusPublisher(_bus, _time);
        _sut = new RunTrackingService(_bus, _runs, _resolutions, NullLogger<RunTrackingService>.Instance);
        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
        (_runs as IDisposable)?.Dispose();
        (_resolutions as IDisposable)?.Dispose();
    }

    private Task<CoreRunRecord> SeedDispatchedAsync(
        Guid commandId, string state = "Dispatched", DateTimeOffset? createdUtc = null)
    {
        var record = new CoreRunRecord
        {
            Id = commandId,
            WorkflowType = "wf-type",
            State = state,
            CreatedUtc = createdUtc ?? _time.Now - TimeSpan.FromMinutes(1),
            UpdatedUtc = createdUtc ?? _time.Now - TimeSpan.FromMinutes(1),
            CommandId = commandId,
            DispatchCommandJson = """{"CommandId":"stub"}"""
        };
        return _runs.SaveAsync(record).ContinueWith(_ => record);
    }

    [Test]
    public async Task FirstStatusEvent_WithoutDispatchRecord_CreatesTheRun()
    {
        var instanceId = Guid.NewGuid();
        await _publisher.PublishAsync(instanceId, "wf-type", "Received");

        var record = await _runs.ReadAsync(instanceId);
        Assert.That(record, Is.Not.Null);
        Assert.That(record!.State, Is.EqualTo("Received"));
    }

    [Test]
    public async Task ClaimEvent_RekeysTheDispatchRecord_OntoTheInstanceId()
    {
        var commandId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var dispatched = await SeedDispatchedAsync(commandId);
        var owner = Guid.NewGuid();

        await _publisher.PublishAsync(
            instanceId, "wf-type", "Received", ownerServiceId: owner, commandId: commandId);

        var rekeyed = await _runs.ReadAsync(instanceId);
        Assert.Multiple(() =>
        {
            Assert.That(rekeyed, Is.Not.Null);
            Assert.That(rekeyed!.State, Is.EqualTo("Received"));
            Assert.That(rekeyed.CreatedUtc, Is.EqualTo(dispatched.CreatedUtc),
                "The run's creation time is the DISPATCH time, not the claim time.");
            Assert.That(rekeyed.DispatchCommandJson, Is.EqualTo(dispatched.DispatchCommandJson));
            Assert.That(rekeyed.OwnerServiceId, Is.EqualTo(owner));
            Assert.That(rekeyed.CommandId, Is.EqualTo(commandId));
        });
        Assert.That(await _runs.ReadAsync(commandId), Is.Null,
            "The command-keyed dispatch record must be deleted after the rekey.");
    }

    [Test]
    public async Task DuplicateClaim_IsIdempotent()
    {
        var commandId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        await SeedDispatchedAsync(commandId);

        await _publisher.PublishAsync(instanceId, "wf-type", "Received", commandId: commandId);
        await _publisher.PublishAsync(instanceId, "wf-type", "Received", commandId: commandId);

        Assert.That(await _runs.ReadAsync(instanceId), Is.Not.Null);
        Assert.That(await _runs.ReadAsync(commandId), Is.Null);
    }

    [Test]
    public async Task TerminalRecord_IsNeverResurrected_ByNonTerminalEvents()
    {
        var instanceId = Guid.NewGuid();
        await _publisher.PublishAsync(instanceId, "wf-type", "Failed", "steering-instance-lost");

        await _publisher.PublishAsync(instanceId, "wf-type", "Running");

        var record = await _runs.ReadAsync(instanceId);
        Assert.Multiple(() =>
        {
            Assert.That(record!.State, Is.EqualTo("Failed"),
                "A late event from a stale command must not resurrect a run the Core declared dead.");
            Assert.That(record.ErrorMessage, Is.EqualTo("steering-instance-lost"));
        });
    }

    [Test]
    public async Task ClaimAgainstAFinalizedDispatch_IsDropped()
    {
        var commandId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        // The claim-timeout sweep already failed this dispatch.
        await SeedDispatchedAsync(commandId, state: "Failed");

        await _publisher.PublishAsync(instanceId, "wf-type", "Received", commandId: commandId);

        Assert.Multiple(async () =>
        {
            Assert.That(await _runs.ReadAsync(instanceId), Is.Null,
                "A late claimant of a dead dispatch must not create a second run identity.");
            Assert.That((await _runs.ReadAsync(commandId))!.State, Is.EqualTo("Failed"),
                "The Core's verdict stands.");
        });
    }

    [Test]
    public async Task CompletedUtc_IsStampedOnFirstTerminal_AndPreserved()
    {
        var instanceId = Guid.NewGuid();
        await _publisher.PublishAsync(instanceId, "wf-type", "Running");
        var terminalTime = _time.Now;
        await _publisher.PublishAsync(instanceId, "wf-type", "Success");
        _time.Now += TimeSpan.FromMinutes(5);
        await _publisher.PublishAsync(instanceId, "wf-type", "Success");

        Assert.That((await _runs.ReadAsync(instanceId))!.CompletedUtc, Is.EqualTo(terminalTime),
            "A late duplicate terminal event must not shift the completion time.");
    }

    [Test]
    public async Task RunStatesVocabulary_CoversThePlatformStates()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RunStates.IsTerminal("Success"), Is.True);
            Assert.That(RunStates.IsTerminal("Failed"), Is.True);
            Assert.That(RunStates.IsTerminal("Cancelled"), Is.True);
            Assert.That(RunStates.IsTerminal("PreFlightFailed"), Is.True);
            Assert.That(RunStates.IsTerminal("Running"), Is.False);
            Assert.That(RunStates.IsTerminal(null), Is.False);
            Assert.That(RunStates.IsActive(RunStates.Dispatched), Is.True);
            Assert.That(RunStates.IsActive("Received"), Is.True);
            Assert.That(RunStates.IsActive("Queued"), Is.True);
            Assert.That(RunStates.IsActive("Running"), Is.True);
            Assert.That(RunStates.IsActive("Draining"), Is.True);
            Assert.That(RunStates.IsActive("Success"), Is.False);
        });
        await Task.CompletedTask;
    }
}

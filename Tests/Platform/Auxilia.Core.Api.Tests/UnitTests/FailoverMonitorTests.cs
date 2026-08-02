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
    private SlotCredentialResolver _resolver = null!;
    private WorkflowTypeRegistryService _registry = null!;
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

        // The re-dispatch path IS RunService.RerunAsync — wire a real one over in-memory stores.
        var protector = new Auxilia.PlatformData.Protection.AesGcmSettingsProtector(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var connectorStore = new InMemoryDataAccess<CoreConnectorRecord>();
        var connectors = new ConnectorService(connectorStore, protector, _time);
        _resolver = new SlotCredentialResolver(
            new InMemoryDataAccess<CoreRunResolutionRecord>(), connectors,
            new ConnectorTokenRefresher(
                connectors,
                new ProviderCatalogService(
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.SlotProviderRecord>(),
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.ProviderCatalogRecord>(),
                    new AuditLog(_audit, _time)),
                new StubHttpClientFactory(new StubHttpMessageHandler(
                    _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))),
                _time, NullLogger<ConnectorTokenRefresher>.Instance),
            new DelegatedTokenStore(new InMemoryDataAccess<Auxilia.Core.Api.Data.DelegatedUserTokenRecord>(), protector, _time),
            new NullDelegatedTokenExchange(), new AuditLog(_audit, _time), _time, Options.Create(_settings));
        var typeStore = new InMemoryDataAccess<CoreWorkflowTypeRecord>();
        _registry = new WorkflowTypeRegistryService(
            typeStore,
            new StubHttpClientFactory(new StubHttpMessageHandler(
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))),
            Options.Create(_settings), new AuditLog(_audit, _time), _time,
            NullLogger<WorkflowTypeRegistryService>.Instance);
        var providerCatalog = new ProviderCatalogService(
            new InMemoryDataAccess<Auxilia.PlatformData.Entities.SlotProviderRecord>(),
            new InMemoryDataAccess<Auxilia.PlatformData.Entities.ProviderCatalogRecord>(),
            new AuditLog(_audit, _time));
        var runService = new RunService(
            _bus,
            new RunConfigurationService(
                new InMemoryDataAccess<CoreRunConfigurationRecord>(),
                new AccessGrantEvaluator(
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.PrincipalRecord>(),
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.GroupMembershipRecord>()),
                _time),
            _registry, new WorkflowSchemaReadService(typeStore), providerCatalog, _resolver,
            new ConnectorAccessPolicy(connectorStore,
                new AccessGrantEvaluator(
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.PrincipalRecord>(),
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.GroupMembershipRecord>())),
            connectors, new RunnerLivenessTracker(), _time,
            Options.Create(_settings), NullLogger<RunService>.Instance);

        _sut = new FailoverMonitor(
            _bus, _liveness, _runs, runService,
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
        var commandId = Guid.NewGuid();
        var command = new RunWorkflowCommand(
            commandId, "wf-type", "docker://wf:test",
            commandContext ?? new Dictionary<string, string>(), ResolutionToken: "token-1");
        // A redispatch goes through RerunAsync: the type must be registered Active and the
        // original resolution context stashed — exactly like a real dispatch leaves behind.
        await _registry.EnsureSeededAsync(
            new StaticWorkflowType { WorkflowType = "wf-type", PackageUri = "docker://wf:test" },
            CancellationToken.None);
        await _resolver.StashAsync(commandId, "token-1", [], triggeredBy: null);
        var run = new CoreRunRecord
        {
            Id = runId,
            WorkflowType = "wf-type",
            State = state,
            CreatedUtc = _time.Now - TimeSpan.FromMinutes(5),
            UpdatedUtc = _time.Now - TimeSpan.FromMinutes(5),
            OwnerServiceId = serviceId,
            CommandId = commandId,
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

    private async Task<CoreRunRecord> SeedZombieRunAsync(TimeSpan sinceUpdate)
    {
        // The owner NEVER heartbeated at this Core (runner restarted with a fresh ServiceId,
        // or the Core restarted and lost its liveness memory).
        var run = new CoreRunRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "wf-type",
            State = "Running",
            CreatedUtc = _time.Now - sinceUpdate,
            UpdatedUtc = _time.Now - sinceUpdate,
            OwnerServiceId = Guid.NewGuid(),
        };
        await _runs.SaveAsync(run);
        return run;
    }

    [Test]
    public async Task ZombieRun_UnknownOwnerAndStale_IsFailedOver()
    {
        _sut.MarkStarted(_time.Now - TimeSpan.FromMinutes(10));
        var zombie = await SeedZombieRunAsync(TimeSpan.FromMinutes(5));

        await _sut.ScanOnceAsync(CancellationToken.None);

        var updated = await _runs.ReadAsync(zombie.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updated!.State, Is.EqualTo("Failed"),
                "A run whose owner was never heard from can never go heartbeat-stale — the sweep must catch it.");
            Assert.That(updated.ErrorMessage, Is.EqualTo("steering-instance-lost"));
        });
    }

    [Test]
    public async Task ZombieSweep_WaitsOutTheGracePeriod_AfterMonitorStart()
    {
        _sut.MarkStarted(_time.Now); // the Core just started — runners haven't beaten yet
        var zombie = await SeedZombieRunAsync(TimeSpan.FromMinutes(5));

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.That((await _runs.ReadAsync(zombie.Id))!.State, Is.EqualTo("Running"),
            "Right after a Core start every owner looks unknown — the sweep must wait a full heartbeat window.");
    }

    [Test]
    public async Task ZombieSweep_LeavesFreshRunsAlone()
    {
        _sut.MarkStarted(_time.Now - TimeSpan.FromMinutes(10));
        var fresh = await SeedZombieRunAsync(TimeSpan.FromSeconds(5)); // updated moments ago

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.That((await _runs.ReadAsync(fresh.Id))!.State, Is.EqualTo("Running"),
            "A recently updated run may simply not have been claimed/beaten yet.");
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

using System.Text.Json;
using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
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

    /// <summary>
    /// Pass-through run store with a one-shot hook after the full-table read — the seam to
    /// interleave "a runner's claim was processed on another node" between the sweep's snapshot
    /// and its per-run work.
    /// </summary>
    private sealed class InterceptingRunStore(IDataAccess<CoreRunRecord> inner) : IDataAccess<CoreRunRecord>
    {
        public Func<Task>? AfterSnapshotRead { get; set; }

        public IObservable<CoreRunRecord> EntityAdded => inner.EntityAdded;
        public IObservable<CoreRunRecord> EntityUpdated => inner.EntityUpdated;
        public IObservable<CoreRunRecord> EntityRemoved => inner.EntityRemoved;

        public async Task<IQueryable<CoreRunRecord>> ReadAsync(CancellationToken ct)
        {
            var snapshot = await inner.ReadAsync(ct);
            if (AfterSnapshotRead is { } hook)
            {
                AfterSnapshotRead = null; // one-shot
                await hook();
            }
            return snapshot;
        }

        public Func<Guid, Task>? AfterReadById { get; set; }

        public async Task<CoreRunRecord?> ReadAsync(Guid id, CancellationToken ct)
        {
            var result = await inner.ReadAsync(id, ct);
            if (AfterReadById is { } hook)
            {
                AfterReadById = null; // one-shot
                await hook(id);
            }
            return result;
        }

        public Task<bool> SaveAsync(CoreRunRecord entity, CancellationToken ct) => inner.SaveAsync(entity, ct);

        public Task<bool> TrySaveAsync(CoreRunRecord entity, long expectedVersion, CancellationToken ct)
            => inner.TrySaveAsync(entity, expectedVersion, ct);

        public Task<bool> RemoveAsync(Guid id) => inner.RemoveAsync(id);
        public Task<bool> RemoveAsync(Guid id, CancellationToken ct) => inner.RemoveAsync(id, ct);
    }

    private ManualTimeProvider _time = null!;
    private FakeMessageBusClient _bus = null!;
    private RunnerLivenessTracker _liveness = null!;
    private InMemoryDataAccess<CoreRunRecord> _innerRuns = null!;
    private InterceptingRunStore _runsInterceptor = null!;
    private IDataAccess<CoreRunRecord> _runs = null!;
    private IDataAccess<CoreRunResolutionRecord> _resolutions = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private InMemoryDataAccess<CoreConnectorRecord> _connectorStore = null!;
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
        _innerRuns = new InMemoryDataAccess<CoreRunRecord>();
        _runsInterceptor = new InterceptingRunStore(_innerRuns);
        _runs = _runsInterceptor;
        _resolutions = new InMemoryDataAccess<CoreRunResolutionRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _settings = new CoreApiSettings { HeartbeatTimeoutSeconds = 30, FailoverScanIntervalSeconds = 3600 };

        // The re-dispatch path IS RunService.RerunAsync — wire a real one over in-memory stores.
        var protector = new Auxilia.PlatformData.Protection.AesGcmSettingsProtector(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var connectorStore = _connectorStore = new InMemoryDataAccess<CoreConnectorRecord>();
        var connectors = new ConnectorService(
            connectorStore, protector,
            new AccessGrantEvaluator(
                new InMemoryDataAccess<PrincipalRecord>(), new InMemoryDataAccess<GroupMembershipRecord>()),
            _time);
        var providerCatalog = new ProviderCatalogService(
            new InMemoryDataAccess<Auxilia.PlatformData.Entities.SlotProviderRecord>(),
            new InMemoryDataAccess<Auxilia.PlatformData.Entities.ProviderCatalogRecord>(),
            new AuditLog(_audit, _time));
        var workspaces = new WorkspaceResourceService(
            new InMemoryDataAccess<CoreWorkspaceRecord>(),
            new AccessGrantEvaluator(
                new InMemoryDataAccess<Auxilia.PlatformData.Entities.PrincipalRecord>(),
                new InMemoryDataAccess<Auxilia.PlatformData.Entities.GroupMembershipRecord>()),
            _time);
        var bindingSecrets = new SlotBindingSecrets(providerCatalog, connectors, workspaces, protector);
        _resolver = new SlotCredentialResolver(
            _resolutions, _runs, connectors,
            new ConnectorTokenRefresher(
                connectors, providerCatalog,
                new StubHttpClientFactory(new StubHttpMessageHandler(
                    _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))),
                _time, NullLogger<ConnectorTokenRefresher>.Instance),
            new DelegatedTokenStore(new InMemoryDataAccess<Auxilia.Core.Api.Data.DelegatedUserTokenRecord>(), protector, _time),
            new NullDelegatedTokenExchange(), bindingSecrets, new AuditLog(_audit, _time), _time, Options.Create(_settings));
        var typeStore = new InMemoryDataAccess<CoreWorkflowTypeRecord>();
        _registry = new WorkflowTypeRegistryService(
            typeStore,
            new StubHttpClientFactory(new StubHttpMessageHandler(
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))),
            Options.Create(_settings), new AuditLog(_audit, _time), _time,
            NullLogger<WorkflowTypeRegistryService>.Instance);
        var runService = new RunService(
            _bus,
            new RunConfigurationService(
                new InMemoryDataAccess<CoreRunConfigurationRecord>(),
                new AccessGrantEvaluator(
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.PrincipalRecord>(),
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.GroupMembershipRecord>()),
                bindingSecrets, _time),
            _registry, new WorkflowSchemaReadService(typeStore), providerCatalog, _resolver, bindingSecrets,
            new ConnectorAccessPolicy(connectorStore,
                new AccessGrantEvaluator(
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.PrincipalRecord>(),
                    new InMemoryDataAccess<Auxilia.PlatformData.Entities.GroupMembershipRecord>())),
            connectors,
            workspaces,
            new AccessGrantEvaluator(
                new InMemoryDataAccess<Auxilia.PlatformData.Entities.PrincipalRecord>(),
                new InMemoryDataAccess<Auxilia.PlatformData.Entities.GroupMembershipRecord>()),
            TestResourceAccess.EmptyPrincipalDirectory(),
            TestResourceAccess.Open,
            new RunnerLivenessTracker(),
            new EnvironmentBaseService(
                new InMemoryDataAccess<EnvironmentBaseRecord>(), new AuditLog(_audit, _time), _time),
            _runs,
            new WorkflowStatusPublisher(_bus, _time), _time,
            Options.Create(_settings), NullLogger<RunService>.Instance);

        _sut = new FailoverMonitor(
            _bus, _liveness, _runs, _resolutions, runService,
            new WorkflowStatusPublisher(_bus, _time),
            new AuditLog(_audit, _time),
            _time, Options.Create(_settings),
            NullLogger<FailoverMonitor>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _innerRuns.Dispose();
        _connectorStore.Dispose();
        (_resolutions as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private async Task<(Guid ServiceId, CoreRunRecord Run)> SeedDeadRunnerWithRunAsync(
        string state = "Running", IReadOnlyDictionary<string, string>? commandContext = null,
        IReadOnlyList<SlotBinding>? bindings = null, Guid? triggeredBy = null)
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
        await _resolver.StashAsync(commandId, "token-1", bindings ?? [], triggeredBy);
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
    public async Task StaleRunner_OrphanOverAPersonalConnector_IsRedispatchedAsTheOriginalPrincipal()
    {
        var owner = Guid.NewGuid();
        var connectorId = Guid.NewGuid();
        await _connectorStore.SaveAsync(new CoreConnectorRecord
        {
            Id = connectorId,
            Name = "personal-github",
            ProviderType = "github",
            Scope = Auxilia.Core.Contracts.ResourceScope.Personal,
            OwnerPrincipalId = owner,
            GrantsJson = "[]"
        });
        var (_, run) = await SeedDeadRunnerWithRunAsync(
            bindings: [new SlotBinding("repo", ConnectorId: connectorId)], triggeredBy: owner);

        await _sut.ScanOnceAsync(CancellationToken.None);

        var redispatch = _bus.PublishedMessages
            .Where(p => p.Topic == _settings.RunCommandQueue)
            .Select(p => p.Message).OfType<RunWorkflowCommand>().SingleOrDefault();
        Assert.That(redispatch, Is.Not.Null,
            "The re-dispatch must act as the ORIGINAL triggering principal — a null principal is "
            + "refused every personal connector, so the failover could never re-dispatch such a run.");
        Assert.That((await _resolutions.ReadAsync(redispatch!.CommandId))!.TriggeredByPrincipalId,
            Is.EqualTo(owner), "The re-stash carries the original principal for the JIT resolution.");
        Assert.That((await _audit.ReadAsync()).Any(a => a.Action == "workflow.redispatched"), Is.True);
        Assert.That((await _runs.ReadAsync(run.Id))!.State, Is.EqualTo("Failed"));
    }

    [Test]
    public async Task StaleRunner_RunCompletedAfterTheSnapshot_IsNeitherOverwrittenNorRedispatched()
    {
        var (_, run) = await SeedDeadRunnerWithRunAsync();
        // Between the sweep's snapshot and the per-run failover, the run's terminal verdict lands
        // (a late runner event on another node, or another Core.Api node's failover).
        _runsInterceptor.AfterSnapshotRead = async () =>
        {
            var current = (await _runs.ReadAsync(run.Id))!;
            await _runs.SaveAsync(current with { State = "Success", UpdatedUtc = _time.Now });
        };

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That((await _runs.ReadAsync(run.Id))!.State, Is.EqualTo("Success"),
                "A run that is already terminal must not be overwritten with Failed.");
            Assert.That(_bus.PublishedMessages.Where(p => p.Topic == _settings.RunCommandQueue), Is.Empty,
                "A completed run must not be re-dispatched.");
            Assert.That(_bus.PublishedMessages.Any(p => p.Topic == $"workflow-cancel-{run.Id}"), Is.False,
                "No cancel for a run that already ended.");
            Assert.That(_bus.PublishedMessages.Any(p =>
                p.Message is WorkflowStatusEvent e && e.State == "Failed"), Is.False,
                "No Failed verdict may be published over a terminal run.");
        });
    }

    [Test]
    public async Task StaleRunner_LostTerminalSwap_DoesNotActOnTheRun()
    {
        var (_, run) = await SeedDeadRunnerWithRunAsync();
        // The record's version moves on between the failover's fresh re-read and its conditional
        // save — the way another Core.Api node's concurrent failover looks from here.
        _runsInterceptor.AfterReadById = async id =>
        {
            var current = (await _innerRuns.ReadAsync(id))!;
            await _innerRuns.SaveAsync(current with
            {
                State = "Failed", ErrorMessage = "steering-instance-lost", UpdatedUtc = _time.Now
            });
        };

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_bus.PublishedMessages.Where(p => p.Topic == _settings.RunCommandQueue), Is.Empty,
                "Only the winner of the terminal swap re-dispatches — never both nodes.");
            Assert.That(_bus.PublishedMessages.Any(p => p.Topic == $"workflow-cancel-{run.Id}"), Is.False);
        });
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

    /// <summary>Seeds exactly what a real dispatch leaves behind: a Dispatched record keyed by the
    /// command id, the stashed resolution context, and an Active registry entry.</summary>
    private async Task<CoreRunRecord> SeedDispatchedRunAsync(
        TimeSpan age, IReadOnlyDictionary<string, string>? commandContext = null)
    {
        var commandId = Guid.NewGuid();
        var command = new RunWorkflowCommand(
            commandId, "wf-type", "docker://wf:test",
            commandContext ?? new Dictionary<string, string>(), ResolutionToken: "token-d");
        await _registry.EnsureSeededAsync(
            new StaticWorkflowType { WorkflowType = "wf-type", PackageUri = "docker://wf:test" },
            CancellationToken.None);
        await _resolver.StashAsync(commandId, "token-d", [], triggeredBy: null);
        var run = new CoreRunRecord
        {
            Id = commandId,
            WorkflowType = "wf-type",
            State = Auxilia.Core.Contracts.RunStates.Dispatched,
            CreatedUtc = _time.Now - age,
            UpdatedUtc = _time.Now - age,
            CommandId = commandId,
            DispatchCommandJson = JsonSerializer.Serialize(command)
        };
        await _runs.SaveAsync(run);
        return run;
    }

    [Test]
    public async Task DispatchedNeverClaimed_PastTimeout_IsFailedRedispatchedAndStashDeleted()
    {
        var run = await SeedDispatchedRunAsync(TimeSpan.FromSeconds(_settings.DispatchClaimTimeoutSeconds + 1));

        await _sut.ScanOnceAsync(CancellationToken.None);

        var updated = await _runs.ReadAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updated!.State, Is.EqualTo("Failed"));
            Assert.That(updated.ErrorMessage, Is.EqualTo("dispatch-never-claimed"));
            Assert.That(_bus.PublishedMessages.Any(p =>
                p.Message is WorkflowStatusEvent e && e.State == "Failed" && e.WorkflowInstanceId == run.Id), Is.True,
                "The timeout must be surfaced as a status event.");
        });

        var redispatch = _bus.PublishedMessages
            .Where(p => p.Topic == _settings.RunCommandQueue)
            .Select(p => p.Message).OfType<RunWorkflowCommand>().SingleOrDefault();
        Assert.That(redispatch, Is.Not.Null, "An unclaimed dispatch is re-dispatched once, guarded.");
        Assert.That(redispatch!.Context.ContainsKey(FailoverMonitor.FailoverContextKey), Is.True);

        Assert.That(await _resolutions.ReadAsync(run.Id), Is.Null,
            "The stale command's stash must be deleted so a late launch cannot resolve credentials.");
        Assert.That((await _audit.ReadAsync()).Any(a => a.Action == "workflow.dispatch-timeout"), Is.True);
    }

    [Test]
    public async Task ClaimProcessedBetweenSnapshotAndSweep_SweepLeavesTheLiveRunAlone()
    {
        var run = await SeedDispatchedRunAsync(TimeSpan.FromSeconds(_settings.DispatchClaimTimeoutSeconds + 1));
        var instanceId = Guid.NewGuid();
        // Between the sweep's snapshot and its per-run work, the runner's "Received" claim is
        // processed (on any node): the dispatch record is rekeyed onto the instance id and the
        // command-keyed row deleted — exactly what RunTrackingService's rekey-on-claim does.
        _runsInterceptor.AfterSnapshotRead = async () =>
        {
            await _runs.SaveAsync(run with { Id = instanceId, State = "Received", UpdatedUtc = _time.Now });
            await _runs.RemoveAsync(run.Id);
        };

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(_bus.PublishedMessages.Where(p => p.Topic == _settings.RunCommandQueue), Is.Empty,
                "The sweep must not re-dispatch a duplicate of the now-live run.");
            Assert.That(await _runs.ReadAsync(run.Id), Is.Null,
                "The rekeyed-away command row must not be resurrected as a Failed ghost.");
            Assert.That((await _runs.ReadAsync(instanceId))!.State, Is.EqualTo("Received"),
                "The live run must be untouched.");
            Assert.That(await _resolutions.ReadAsync(run.Id), Is.Not.Null,
                "The resolution stash the live run's JIT slot activations need must survive.");
            Assert.That(_bus.PublishedMessages.Any(p =>
                p.Message is WorkflowStatusEvent e && e.State == "Failed"), Is.False,
                "No Failed verdict may be published for a claimed run.");
        });
    }

    [Test]
    public async Task DispatchRefreshedBetweenSnapshotAndSweep_IsSkipped()
    {
        var run = await SeedDispatchedRunAsync(TimeSpan.FromSeconds(_settings.DispatchClaimTimeoutSeconds + 1));
        // The record moved past the claim cutoff after the snapshot — the fresh re-read must skip it.
        _runsInterceptor.AfterSnapshotRead = () => _runs.SaveAsync(run with { UpdatedUtc = _time.Now });

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That((await _runs.ReadAsync(run.Id))!.State,
                Is.EqualTo(Auxilia.Core.Contracts.RunStates.Dispatched));
            Assert.That(_bus.PublishedMessages.Where(p => p.Topic == _settings.RunCommandQueue), Is.Empty);
            Assert.That(await _resolutions.ReadAsync(run.Id), Is.Not.Null);
        });
    }

    [Test]
    public async Task DispatchedRun_WithinClaimWindow_IsUntouched()
    {
        var run = await SeedDispatchedRunAsync(TimeSpan.FromSeconds(10));

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.That((await _runs.ReadAsync(run.Id))!.State,
            Is.EqualTo(Auxilia.Core.Contracts.RunStates.Dispatched));
    }

    [Test]
    public async Task DispatchedSweep_IsSkipped_UnderAllowDispatchWithoutRunner()
    {
        _settings.AllowDispatchWithoutRunner = true;
        var run = await SeedDispatchedRunAsync(TimeSpan.FromHours(2));

        await _sut.ScanOnceAsync(CancellationToken.None);

        Assert.That((await _runs.ReadAsync(run.Id))!.State,
            Is.EqualTo(Auxilia.Core.Contracts.RunStates.Dispatched),
            "AllowDispatchWithoutRunner exists precisely to queue deliberately — never sweep under it.");
    }

    [Test]
    public async Task DispatchedRun_IsExcludedFromTheZombieSweep()
    {
        _sut.MarkStarted(_time.Now - TimeSpan.FromMinutes(10));
        // Older than the 30s heartbeat window (zombie-eligible) but inside the claim window.
        var run = await SeedDispatchedRunAsync(TimeSpan.FromSeconds(60));

        await _sut.ScanOnceAsync(CancellationToken.None);

        var updated = await _runs.ReadAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updated!.State, Is.EqualTo(Auxilia.Core.Contracts.RunStates.Dispatched),
                "Dispatched runs belong to the claim-timeout sweep, not the zombie sweep.");
            Assert.That(updated.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public async Task ResolutionRecords_PastRetention_ArePurged()
    {
        _time.Now -= TimeSpan.FromDays(40);
        await _resolver.StashAsync(Guid.NewGuid(), "old-token", [], triggeredBy: null);
        _time.Now += TimeSpan.FromDays(40);
        await _resolver.StashAsync(Guid.NewGuid(), "fresh-token", [], triggeredBy: null);

        await _sut.ScanOnceAsync(CancellationToken.None);

        var remaining = (await _resolutions.ReadAsync()).ToList();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].ResolutionTokenHash,
            Is.EqualTo(Auxilia.Core.Api.Services.ResolutionTokens.Hash("fresh-token")),
            "the stash keeps only the token's digest, keyed to the surviving record");
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

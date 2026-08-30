using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Core.Api failover monitor. Tracks Core.Runner liveness purely over the bus (via
/// <see cref="RunnerHeartbeat"/>) and, when a runner's beat goes stale, fails over each of its
/// non-terminal runs from the Core's OWN store: cancels via the run's cancel queue, marks the run
/// <c>Failed</c> (<c>steering-instance-lost</c>), audits, publishes a <c>Failed</c> status event, and
/// re-dispatches the stored dispatch command once — guarded by <c>FAILOVER_REDISPATCH</c> so a run
/// that dies again is never re-dispatched a second time. It never reads the runner's database,
/// preserving the Core/Runner DB split.
/// </summary>
public sealed class FailoverMonitor(
    IMessageBusClient bus,
    RunnerLivenessTracker liveness,
    IDataAccess<CoreRunRecord> runs,
    IDataAccess<CoreRunResolutionRecord> resolutions,
    RunService runService,
    WorkflowStatusPublisher statusPublisher,
    AuditLog auditLog,
    TimeProvider clock,
    IOptions<CoreApiSettings> settings,
    ILogger<FailoverMonitor> logger) : IHostedService
{
    /// <summary>Context key marking a run that is itself a failover re-dispatch — never re-dispatched again.</summary>
    internal const string FailoverContextKey = "FAILOVER_REDISPATCH";

    private IAsyncDisposable? _subscription;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastResolutionPurge = DateTimeOffset.MinValue;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareExchangeAsync(RunnerHeartbeat.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToExchangeAsync<RunnerHeartbeat>(
            RunnerHeartbeat.ExchangeName, OnHeartbeatAsync, cancellationToken);

        _startedAt = clock.GetUtcNow();
        _loopCts = new CancellationTokenSource();
        _loop = RunLoopAsync(_loopCts.Token);

        logger.LogInformation(
            "FailoverMonitor started. ScanInterval={Interval}s Timeout={Timeout}s",
            settings.Value.FailoverScanIntervalSeconds, settings.Value.HeartbeatTimeoutSeconds);
    }

    private Task OnHeartbeatAsync(RunnerHeartbeat heartbeat, CancellationToken ct)
    {
        // Stamp with our own receive time, not the runner's clock — robust to cross-host skew.
        liveness.Record(heartbeat.ServiceId, clock.GetUtcNow(),
            heartbeat.ServiceName, heartbeat.HostPlatform, heartbeat.HostArchitecture);
        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.FailoverScanIntervalSeconds));
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await ScanOnceAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failover scan failed — will retry next interval.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Test hook: stamps the monitor-start time the zombie grace period counts from.</summary>
    internal void MarkStarted(DateTimeOffset at) => _startedAt = at;

    /// <summary>One failover sweep: fail over the runs of every runner whose beat is now stale.</summary>
    internal async Task ScanOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var timeout = TimeSpan.FromSeconds(settings.Value.HeartbeatTimeoutSeconds);
        var cutoff = now - timeout;
        var dead = liveness.DeadSince(cutoff);
        var graceElapsed = now - _startedAt >= timeout;

        var allRuns = (await runs.ReadAsync(ct)).ToList();
        await SweepUnclaimedDispatchesAsync(allRuns, now, ct);
        await PurgeExpiredResolutionRecordsAsync(now, ct);

        if (dead.Count == 0 && !graceElapsed)
            return;
        var handled = new HashSet<Guid>();
        foreach (var serviceId in dead)
        {
            var orphans = allRuns
                .Where(r => r.OwnerServiceId == serviceId && !CoreRunStates.IsTerminal(r.State))
                .ToList();

            logger.LogWarning(
                "Core.Runner {ServiceId} bus heartbeat is stale (cutoff {Cutoff:O}) — failing over {Count} run(s).",
                serviceId, cutoff, orphans.Count);

            foreach (var orphan in orphans)
            {
                await FailOverAsync(orphan, ct);
                handled.Add(orphan.Id);
            }

            // One failover per death: forget the runner so a re-appearing beat is tracked afresh.
            liveness.Forget(serviceId);
        }

        // ZOMBIE sweep: a non-terminal run whose owner this Core has NEVER heard from (runner
        // restarted with a fresh ServiceId, or the Core itself restarted and lost the liveness
        // memory) would linger as Running forever — its heartbeat can never go "stale" because
        // it was never seen. After a full heartbeat window of grace since monitor start, such
        // stale-by-update-time runs are failed over like any other orphan.
        if (!graceElapsed)
            return;
        var zombies = allRuns
            .Where(r => !handled.Contains(r.Id)
                        && !CoreRunStates.IsTerminal(r.State)
                        // Dispatched runs have no owner BY DESIGN (nothing claimed them yet) —
                        // they belong to the claim-timeout sweep, with its own window + message.
                        && r.State != Auxilia.Core.Contracts.RunStates.Dispatched
                        && r.UpdatedUtc < cutoff
                        && (r.OwnerServiceId is not { } owner || !liveness.IsKnown(owner)))
            .ToList();
        foreach (var zombie in zombies)
        {
            logger.LogWarning(
                "Run {RunId} is a zombie: state {State}, owner {Owner} never heartbeated, last update {Updated:O} — failing it over.",
                zombie.Id, zombie.State, zombie.OwnerServiceId, zombie.UpdatedUtc);
            await FailOverAsync(zombie, ct);
        }
    }

    /// <summary>
    /// Fails over dispatches no runner ever claimed. With the run record born at dispatch time
    /// (keyed by the command id), a command lost before its "Received" claim is visible here
    /// instead of leaving no trace. Skipped under <c>AllowDispatchWithoutRunner</c>, which exists
    /// precisely to queue deliberately while no runner is attached.
    /// </summary>
    private async Task SweepUnclaimedDispatchesAsync(
        List<CoreRunRecord> allRuns, DateTimeOffset now, CancellationToken ct)
    {
        if (settings.Value.AllowDispatchWithoutRunner)
            return;
        var claimCutoff = now - TimeSpan.FromSeconds(settings.Value.DispatchClaimTimeoutSeconds);
        var unclaimed = allRuns
            .Where(r => r.State == Auxilia.Core.Contracts.RunStates.Dispatched && r.UpdatedUtc < claimCutoff)
            .ToList();
        foreach (var run in unclaimed)
        {
            // FRESH RE-READ: the allRuns snapshot can be stale — a runner's "Received" claim may
            // have been processed (rekeying onto the instance id and DELETING this command-keyed
            // row) between the snapshot and this point. Acting on stale state would re-dispatch a
            // duplicate of the now-live run, resurrect the deleted row as a permanent Failed
            // ghost, and delete the resolution stash the live run's JIT slot activations need.
            var current = await runs.ReadAsync(run.Id, ct);
            if (current is null
                || current.State != Auxilia.Core.Contracts.RunStates.Dispatched
                || current.UpdatedUtc >= claimCutoff)
                continue;

            logger.LogWarning(
                "Run {RunId} was dispatched at {Dispatched:O} and never claimed by any runner — failing it over.",
                current.Id, current.UpdatedUtc);

            // Terminal transition FIRST, as a conditional save against the version just observed:
            // only the winner of this swap may re-dispatch and delete the stash. Losing it (a
            // claim landed after the re-read, or another Core.Api node swept first) does nothing.
            var won = await runs.TrySaveAsync(current with
            {
                State = "Failed",
                ErrorMessage = "dispatch-never-claimed",
                UpdatedUtc = now
            }, current.Version, ct);
            if (!won)
                continue;

            await auditLog.AppendAsync("core-api", "workflow.dispatch-timeout",
                current.Id.ToString(), "dispatch-never-claimed", ct: ct);
            await statusPublisher.PublishAsync(current.Id, current.WorkflowType, "Failed",
                "dispatch-never-claimed", commandId: current.CommandId, ct: ct);

            // Re-dispatch AFTER the won terminal swap and BEFORE the stash is deleted below —
            // RerunAsync reads the stash, and a lost swap must not spawn a duplicate.
            await RedispatchAsync(current, ct);

            // Delete the stale command's resolution stash: if a late runner ever dequeues the
            // original command, JIT credential resolution fails and the launch dies pre-flight —
            // the anti-duplicate-execution measure behind the terminal-sink guard.
            await resolutions.RemoveAsync(current.Id, ct);
        }
    }

    /// <summary>
    /// Low-frequency retention purge of dispatch resolution records (they otherwise accumulate
    /// one per dispatch, forever). Rerun only works within the retention window — callers get
    /// the existing graceful "resolution context is no longer stored" error beyond it.
    /// </summary>
    private async Task PurgeExpiredResolutionRecordsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var retentionDays = settings.Value.ResolutionRecordRetentionDays;
        if (retentionDays <= 0 || now - _lastResolutionPurge < TimeSpan.FromHours(1))
            return;
        _lastResolutionPurge = now;
        var cutoff = now - TimeSpan.FromDays(retentionDays);
        var expired = (await resolutions.ReadAsync(ct))
            .Where(r => r.CreatedUtc < cutoff)
            .Select(r => r.Id)
            .ToList();
        foreach (var id in expired)
            await resolutions.RemoveAsync(id, ct);
        if (expired.Count > 0)
            logger.LogInformation(
                "Purged {Count} dispatch resolution record(s) older than {Days} day(s).",
                expired.Count, retentionDays);
    }

    private async Task FailOverAsync(CoreRunRecord orphan, CancellationToken ct)
    {
        // Graceful termination via the run's cancel queue — a dead workflow simply never reads it.
        await bus.PublishAsync($"workflow-cancel-{orphan.Id}", new CancelWorkflowCommand(orphan.Id), ct);

        await runs.SaveAsync(orphan with
        {
            State = "Failed",
            ErrorMessage = "steering-instance-lost",
            UpdatedUtc = clock.GetUtcNow()
        }, ct);

        await auditLog.AppendAsync("core-api", "workflow.failover",
            orphan.Id.ToString(), "steering-instance-lost", ct: ct);
        await statusPublisher.PublishAsync(orphan.Id, orphan.WorkflowType, "Failed",
            "steering-instance-lost", ct: ct);

        await RedispatchAsync(orphan, ct);
    }

    /// <summary>
    /// One guarded re-dispatch of a failed-over run through the SAME machinery as an operator
    /// rerun: a fresh command id + resolution token with the bindings re-stashed under them —
    /// re-publishing the old token under a new id could never resolve credentialed slots.
    /// </summary>
    private async Task RedispatchAsync(CoreRunRecord orphan, CancellationToken ct)
    {
        if (!settings.Value.RedispatchOnFailover || orphan.DispatchCommandJson is null)
            return;

        var original = JsonSerializer.Deserialize<RunWorkflowCommand>(orphan.DispatchCommandJson);
        if (original is null)
            return;

        if (original.Context.ContainsKey(FailoverContextKey))
        {
            logger.LogWarning(
                "Run {RunId} was already a failover re-dispatch — not re-dispatching again.", orphan.Id);
            return;
        }

        try
        {
            var accepted = await runService.RerunAsync(
                orphan, triggeredBy: null, ct,
                contextOverlay: new Dictionary<string, string>
                {
                    [FailoverContextKey] = orphan.Id.ToString("D")
                });
            await auditLog.AppendAsync("core-api", "workflow.redispatched",
                orphan.Id.ToString(), accepted.RunId.ToString(), ct: ct);
            logger.LogInformation(
                "Re-dispatched workflow {WorkflowType} after failover of run {RunId} (new command {CommandId}).",
                orphan.WorkflowType, orphan.Id, accepted.RunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Failover re-dispatch of run {RunId} failed — the run stays Failed.", orphan.Id);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loopCts is not null)
            await _loopCts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

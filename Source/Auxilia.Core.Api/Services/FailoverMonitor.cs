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
        liveness.Record(heartbeat.ServiceId, clock.GetUtcNow());
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
        if (dead.Count == 0 && !graceElapsed)
            return;
        var allRuns = (await runs.ReadAsync(ct)).ToList();
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

        var context = new Dictionary<string, string>(original.Context)
        {
            [FailoverContextKey] = orphan.Id.ToString("D")
        };
        var redispatch = original with { CommandId = Guid.NewGuid(), Context = context };

        await bus.PublishAsync(settings.Value.RunCommandQueue, redispatch, ct);
        await auditLog.AppendAsync("core-api", "workflow.redispatched",
            orphan.Id.ToString(), redispatch.CommandId.ToString(), ct: ct);

        logger.LogInformation(
            "Re-dispatched workflow {WorkflowType} after failover of run {RunId} (new command {CommandId}).",
            orphan.WorkflowType, orphan.Id, redispatch.CommandId);
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

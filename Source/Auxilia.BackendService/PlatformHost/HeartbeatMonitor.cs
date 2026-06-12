using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.PlatformHost;

/// <summary>
/// Watches Steering Instance heartbeats (ARCHITECTURE §14.2). When an instance goes stale,
/// its owned non-terminal runs are terminated via their cancel queues, marked Failed,
/// audited, surfaced as status events, and — per retry policy — re-dispatched once as a
/// fresh run from the stored dispatch command. Recovery is always a restart from scratch.
/// </summary>
public sealed class HeartbeatMonitor(
    IDataAccess<ServiceHeartbeatRecord> heartbeats,
    IDataAccess<WorkflowInstanceRecord> instances,
    IMessageBusClient messageBus,
    WorkflowStatusPublisher statusPublisher,
    AuditLog auditLog,
    TimeProvider timeProvider,
    IOptions<PlatformHostSettings> settings,
    ILogger<HeartbeatMonitor> logger) : BackgroundService
{
    internal const string FailoverContextKey = "FAILOVER_REDISPATCH";
    private static readonly string[] NonTerminalStates = ["Received", "Queued", "Running", "WaitingForInput", "Draining"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.MonitorIntervalSeconds));
        using var timer = new PeriodicTimer(interval, timeProvider);

        logger.LogInformation("HeartbeatMonitor started. Interval={Interval}s Timeout={Timeout}s",
            interval.TotalSeconds, settings.Value.HeartbeatTimeoutSeconds);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Heartbeat scan failed — will retry next interval.");
            }
        }
    }

    internal async Task ScanAsync(CancellationToken ct)
    {
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromSeconds(settings.Value.HeartbeatTimeoutSeconds);

        var heartbeatQuery = await heartbeats.ReadAsync(ct);
        var deadServices = heartbeatQuery.Where(h => h.LastBeatUtc < cutoff).ToList();
        if (deadServices.Count == 0)
            return;

        var instanceQuery = await instances.ReadAsync(ct);
        var allInstances = instanceQuery.ToList();

        foreach (var dead in deadServices)
        {
            var orphans = allInstances
                .Where(i => i.OwnerServiceId == dead.Id && NonTerminalStates.Contains(i.State))
                .ToList();

            logger.LogWarning(
                "Steering Instance {ServiceId} heartbeat is stale (last beat {LastBeat:O}) — failing over {Count} run(s).",
                dead.Id, dead.LastBeatUtc, orphans.Count);

            foreach (var orphan in orphans)
                await FailOverAsync(orphan, ct);

            // One failover per death: drop the stale heartbeat so the next scan starts clean.
            await heartbeats.RemoveAsync(dead.Id, ct);
        }
    }

    private async Task FailOverAsync(WorkflowInstanceRecord orphan, CancellationToken ct)
    {
        // Graceful termination via the instance's cancel queue — works without Docker access;
        // a dead workflow simply never reads it.
        await messageBus.PublishAsync($"workflow-cancel-{orphan.Id}",
            new CancelWorkflowCommand(orphan.Id), ct);

        await instances.SaveAsync(orphan with
        {
            State = "Failed",
            CompletedUtc = timeProvider.GetUtcNow(),
            ErrorMessage = "steering-instance-lost"
        }, ct);

        await auditLog.AppendAsync("backend-service", "workflow.failover",
            orphan.Id.ToString(), "steering-instance-lost", ct: ct);
        await statusPublisher.PublishAsync(orphan.Id, orphan.WorkflowType, "Failed",
            "steering-instance-lost", ct);

        if (!settings.Value.RedispatchOnFailover || orphan.DispatchCommandJson is null)
            return;

        var original = JsonSerializer.Deserialize<RunWorkflowCommand>(orphan.DispatchCommandJson);
        if (original is null)
            return;

        if (original.Context.ContainsKey(FailoverContextKey))
        {
            logger.LogWarning(
                "Run {InstanceId} was already a failover re-dispatch — not re-dispatching again.",
                orphan.Id);
            return;
        }

        var context = new Dictionary<string, string>(original.Context)
        {
            [FailoverContextKey] = orphan.Id.ToString("D")
        };
        var redispatch = original with { CommandId = Guid.NewGuid(), Context = context };

        await messageBus.PublishAsync(settings.Value.CommandQueueName, redispatch, ct);
        await auditLog.AppendAsync("backend-service", "workflow.redispatched",
            orphan.Id.ToString(), redispatch.CommandId.ToString(), ct: ct);

        logger.LogInformation(
            "Re-dispatched workflow {WorkflowType} after failover of run {InstanceId} (new command {CommandId}).",
            orphan.WorkflowType, orphan.Id, redispatch.CommandId);
    }
}

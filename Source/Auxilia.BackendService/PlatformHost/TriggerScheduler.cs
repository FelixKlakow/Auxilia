using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.PlatformHost;

/// <summary>
/// The platform scheduler (ARCHITECTURE §6 trigger model): dispatches enabled interval-based
/// triggers as RunWorkflowCommands carrying the configured run-as principal, so scheduled
/// runs pass the same policy checks as manual ones.
/// </summary>
public sealed class TriggerScheduler(
    IDataAccess<ScheduledTriggerRecord> triggers,
    IMessageBusClient messageBus,
    AuditLog auditLog,
    TimeProvider timeProvider,
    IOptions<PlatformHostSettings> settings,
    ILogger<TriggerScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.SchedulerIntervalSeconds));
        using var timer = new PeriodicTimer(interval, timeProvider);

        logger.LogInformation("TriggerScheduler started. Interval={Interval}s", interval.TotalSeconds);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DispatchDueTriggersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trigger scan failed — will retry next interval.");
            }
        }
    }

    internal async Task DispatchDueTriggersAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var query = await triggers.ReadAsync(ct);
        var due = query
            .Where(t => t.Enabled)
            .ToList()
            .Where(t => t.LastDispatchedUtc is null ||
                        now - t.LastDispatchedUtc >= TimeSpan.FromSeconds(t.IntervalSeconds))
            .ToList();

        foreach (var trigger in due)
        {
            var context = trigger.ContextJson is null
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(trigger.ContextJson) ?? [];

            var command = new RunWorkflowCommand(
                Guid.NewGuid(), trigger.WorkflowType, trigger.WorkflowPackageUri,
                context, trigger.RunAsPrincipalId);

            await messageBus.PublishAsync(settings.Value.CommandQueueName, command, ct);
            await triggers.SaveAsync(trigger with { LastDispatchedUtc = now }, ct);
            await auditLog.AppendAsync("backend-service", "trigger.scheduled-dispatch",
                trigger.WorkflowType, command.CommandId.ToString(), ct: ct);

            logger.LogInformation(
                "Scheduled trigger dispatched. WorkflowType={WorkflowType} CommandId={CommandId}",
                trigger.WorkflowType, command.CommandId);
        }
    }
}

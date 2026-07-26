using System.Text.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.Options;

namespace Auxilia.WorkflowStudio.Triggers;

/// <summary>
/// The workflow-domain scheduler (ARCHITECTURE §6 trigger model): dispatches enabled interval-based
/// triggers through the Core Run API (<see cref="ICoreClient"/>) carrying the configured run-as
/// principal, so scheduled runs pass the same policy checks as manual ones. Dispatch goes via the
/// Core — the Studio is a pure Core client and never publishes raw run commands to the bus.
/// </summary>
public sealed class TriggerScheduler(
    IDataAccess<ScheduledTriggerRecord> triggers,
    ICoreClient core,
    AuditLog auditLog,
    TimeProvider timeProvider,
    IOptions<TriggerSettings> settings,
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

            // Configuration-wired triggers (#20) dispatch the stored configuration by id, on behalf of
            // the run-as principal; inline triggers dispatch the registered workflow type directly. Either
            // way the Core is the authorization authority and evaluates the run-as principal.
            var accepted = trigger.WorkflowConfigurationId is { } configId
                ? await core.RunConfigurationAsync(
                    configId, onBehalfOf: trigger.RunAsPrincipalId, context: context, ct)
                : await core.RunAsync(
                    new RunRequest(trigger.WorkflowType, context,
                        RequestedBy: trigger.RunAsPrincipalId), ct);

            await triggers.SaveAsync(trigger with { LastDispatchedUtc = now }, ct);
            await auditLog.AppendAsync("workflow-studio", "trigger.scheduled-dispatch",
                trigger.WorkflowType, accepted.CommandId.ToString(), ct: ct);

            logger.LogInformation(
                "Scheduled trigger dispatched. WorkflowType={WorkflowType} RunId={RunId}",
                trigger.WorkflowType, accepted.RunId);
        }
    }
}

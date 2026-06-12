using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Drain-and-replace for long-living workflows (ARCHITECTURE §6): when a workflow type's
/// stored configuration changes, running long-living instances are signalled to drain
/// (finish in-flight work, accept no new triggers, exit). The replacement starts with the
/// fresh configuration once the drained instance reaches a terminal state
/// (<see cref="WorkflowStateHandler"/> re-dispatches the stored command).
/// </summary>
public sealed class LongLivingDrainCoordinator(
    IMessageBusClient messageBus,
    WorkflowInstanceRegistry instanceRegistry,
    WorkflowStatusPublisher statusPublisher,
    AuditLog auditLog,
    ILogger<LongLivingDrainCoordinator> logger)
{
    public async Task DrainRunningInstancesAsync(string workflowTypeName, CancellationToken ct = default)
    {
        var running = await instanceRegistry.GetRunningLongLivingAsync(workflowTypeName, ct);
        foreach (var instance in running)
        {
            logger.LogInformation(
                "Configuration changed for {WorkflowType} — draining long-living instance {InstanceId}.",
                workflowTypeName, instance.Id);

            await messageBus.PublishAsync($"workflow-drain-{instance.Id}",
                new DrainWorkflowCommand(instance.Id), ct);
            await instanceRegistry.SetStateAsync(instance.Id, "Draining", ct: ct);
            await statusPublisher.PublishAsync(instance.Id, workflowTypeName, "Draining", ct: ct);
            await auditLog.AppendAsync("steering-instance", "workflow.drain-signalled",
                instance.Id.ToString(), "configuration-changed", ct: ct);
        }
    }
}

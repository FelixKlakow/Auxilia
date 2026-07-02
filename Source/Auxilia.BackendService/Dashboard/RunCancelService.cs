using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.BackendService.Dashboard;

/// <summary>
/// Cancellation of an active run from the dashboard: publishes a
/// <see cref="CancelWorkflowCommand"/> onto the Steering Instance's cancel queue — the same
/// path the MCP <c>cancel_workflow</c> tool and the heartbeat monitor use. The run flips to
/// Cancelled once the workflow's cancellation token fires and it reports back.
/// </summary>
public sealed class RunCancelService(
    IDataAccess<WorkflowInstanceRecord> instances,
    IPolicyEngine policyEngine,
    IMessageBusClient messageBus,
    AuditLog auditLog)
{
    public const string CancelQueueName = "workflow.cancel-commands";

    public async Task RequestCancelAsync(Guid actorPrincipalId, Guid instanceId, CancellationToken ct = default)
    {
        var run = await instances.ReadAsync(instanceId, ct)
                  ?? throw new InvalidOperationException("Unknown run.");
        if (WorkflowRerunService.IsTerminal(run))
            throw new InvalidOperationException("This run already finished.");

        var decision = await policyEngine.EvaluateAsync(
            new PolicyContext(actorPrincipalId, PermissionActions.WorkflowCancel, instanceId.ToString())
                { WorkflowType = run.WorkflowType }, ct);
        if (!decision.Allowed)
            throw new InvalidOperationException($"workflow.cancel denied: {decision.Reason}");

        await messageBus.PublishAsync(CancelQueueName, new CancelWorkflowCommand(instanceId), ct);
        await auditLog.AppendAsync(actorPrincipalId.ToString("D"), "workflow.cancel-requested",
            instanceId.ToString(), run.WorkflowType, ct: ct);
    }
}

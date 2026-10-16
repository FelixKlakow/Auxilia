using Auxilia.Messaging;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// The broker topology workload containers talk to. Every exchange a workload publishes on is
/// declared HERE, by the runner, before any launch — a workload identity has no configure
/// permission outside its own queues, so it can never declare (or delete) platform topology.
/// </summary>
public static class WorkloadBusTopology
{
    public static async Task DeclareAsync(IMessageBusClient bus, CancellationToken ct = default)
    {
        await bus.DeclareTopicExchangeAsync(WorkflowQueues.IngressExchangeName, ct);
        await bus.DeclareExchangeAsync(WorkflowQueues.StateExchangeName, ct);
        await bus.DeclareTopicExchangeAsync(ViewDataMessage.ExchangeName, ct);
        await bus.DeclareTopicExchangeAsync(WorkflowEventMessage.ExchangeName, ct);
    }

    /// <summary>
    /// Declares a runner request queue and binds it to the ingress exchange under its own name —
    /// the only way a workload's publish can reach it.
    /// </summary>
    /// <summary>The runner request queues a workload may reach through the ingress exchange.</summary>
    public static IReadOnlyList<string> IngressQueues(WorkflowDispatcherSettings settings) =>
    [
        settings.AnnouncementQueueName,
        settings.RegistrationQueueName,
        settings.SlotActivationQueueName,
        settings.ResourceProxyQueueName,
        settings.PodControlQueueName,
        WorkflowQueues.SignalsQueueName,
    ];

    public static async Task ExposeIngressQueueAsync(IMessageBusClient bus, string queueName, CancellationToken ct = default)
    {
        await bus.DeclareTopicExchangeAsync(WorkflowQueues.IngressExchangeName, ct);
        await bus.DeclareQueueAsync(queueName, ct);
        await bus.BindQueueAsync(queueName, WorkflowQueues.IngressExchangeName, queueName, ct);
    }
}

/// <summary>
/// Issues and revokes the per-instance broker identity a workload container connects with. Issued
/// right before launch; revoked on every terminal path (graceful state, pre-flight failure, crash,
/// re-adoption sweeps) — a finished run's credential stops working at the broker.
/// </summary>
public sealed class WorkloadBusCredentials(
    IBrokerIdentityIssuer issuer,
    Microsoft.Extensions.Options.IOptions<WorkflowDispatcherSettings> settings,
    Auxilia.PlatformData.AuditLog auditLog,
    ILogger<WorkloadBusCredentials> logger)
{
    public async Task<(string UserName, string Password)> IssueAsync(Guid instanceId, CancellationToken ct = default)
    {
        var userName = WorkloadBusIdentity.UserNameFor(instanceId);
        var password = await issuer.IssueAsync(
            userName, WorkloadBusAccess.For(instanceId, WorkloadBusTopology.IngressQueues(settings.Value)), ct);
        await auditLog.AppendAsync("core-runner", "workflow.bus-identity.issued", instanceId.ToString(), userName, ct: ct);
        return (userName, password);
    }

    /// <summary>Best-effort and idempotent: a failure is logged, never thrown.</summary>
    public async Task RevokeAsync(Guid instanceId)
    {
        var userName = WorkloadBusIdentity.UserNameFor(instanceId);
        try
        {
            if (await issuer.RevokeAsync(userName))
                await auditLog.AppendAsync("core-runner", "workflow.bus-identity.revoked", instanceId.ToString(), userName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Revoking the broker identity of {InstanceId} failed.", instanceId);
        }
    }
}

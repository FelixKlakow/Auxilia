using System.Text.RegularExpressions;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Messaging;

/// <summary>
/// The broker permissions of ONE workload instance's identity — the contract between the SDK's
/// queue conventions (<see cref="WorkflowQueues"/>) and the platform: it may declare and consume
/// only its own per-instance queues; publish only to the ingress exchange (under the platform's
/// request queue names), the terminal-state fanout, its OWN view routing key, and non-reserved
/// event types. It can neither read nor write any other queue — the run-command queue, other
/// runs' response queues, the cancel queue — and it never holds a platform credential.
/// </summary>
public static class WorkloadBusAccess
{
    public static BrokerPermissions For(Guid instanceId, IReadOnlyList<string> ingressQueues)
    {
        var ownQueues = "^(" + string.Join("|",
            Regex.Escape(WorkflowQueues.ResponseQueueFor(instanceId)) + "(-resources|-inputs|-pod)?",
            Regex.Escape(WorkflowQueues.CancelQueueFor(instanceId)),
            Regex.Escape(WorkflowQueues.DrainQueueFor(instanceId))) + ")$";

        var writableExchanges = "^(" + string.Join("|",
            Regex.Escape(WorkflowQueues.IngressExchangeName),
            Regex.Escape(WorkflowQueues.StateExchangeName),
            Regex.Escape(ViewDataMessage.ExchangeName),
            Regex.Escape(WorkflowEventMessage.ExchangeName)) + ")$";

        var ingressKeys = "^(" + string.Join("|", ingressQueues.Select(Regex.Escape)) + ")$";

        return new BrokerPermissions(
            Configure: ownQueues,
            Write: writableExchanges,
            Read: ownQueues,
            TopicPermissions:
            [
                new BrokerTopicPermission(WorkflowQueues.IngressExchangeName, ingressKeys, "^$"),
                new BrokerTopicPermission(ViewDataMessage.ExchangeName,
                    "^" + Regex.Escape(ViewDataMessage.RoutingKeyFor(instanceId)) + "$", "^$"),
                new BrokerTopicPermission(WorkflowEventMessage.ExchangeName,
                    "^(?!" + Regex.Escape(WorkflowEventMessage.ReservedPlatformPrefix) + ").+$", "^$"),
            ]);
    }
}

namespace Auxilia.Workflows.Messaging;

/// <summary>
/// Canonical queue-name conventions shared by the SDK and the Core.Runner so the
/// platform can pre-create an instance's response queue before the workflow starts.
/// </summary>
public static class WorkflowQueues
{
    /// <summary>
    /// Topic exchange through which workload identities reach the runner's request queues
    /// (announcement, registration, slot activation, resource proxy, pod control, signals) —
    /// each bound under its own name. Workloads cannot publish to the default exchange, so no
    /// other queue (the run-command queue above all) is reachable from a container.
    /// </summary>
    public const string IngressExchangeName = "workflow.ingress";

    /// <summary>Queue the runner's signal dispatcher consumes (reached via the ingress exchange).</summary>
    public const string SignalsQueueName = "workflow.signals";

    /// <summary>Fanout exchange carrying terminal <c>WorkflowStateMessage</c>s to every runner.</summary>
    public const string StateExchangeName = "workflow.state";

    public static string CancelQueueFor(Guid workflowInstanceId) => $"workflow-cancel-{workflowInstanceId}";

    public static string DrainQueueFor(Guid workflowInstanceId) => $"workflow-drain-{workflowInstanceId}";

    public static string ResponseQueueFor(Guid workflowInstanceId) => $"workflow-response-{workflowInstanceId}";

    /// <summary>
    /// Resource proxy responses use their own per-instance queue: a second subscriber on the
    /// main response queue would compete for slot-activation/configuration messages.
    /// </summary>
    public static string ResourceResponseQueueFor(Guid workflowInstanceId)
        => $"workflow-response-{workflowInstanceId}-resources";

    /// <summary>
    /// Operator inputs (guidance/decisions) use their own per-instance queue for the same
    /// reason: a standing input subscriber on the main response queue would compete for
    /// slot-activation/configuration responses and silently swallow them.
    /// </summary>
    public static string InputQueueFor(Guid workflowInstanceId)
        => $"workflow-response-{workflowInstanceId}-inputs";

    /// <summary>Pod-control responses use their own per-instance queue, like resources/inputs.</summary>
    public static string PodControlResponseQueueFor(Guid workflowInstanceId)
        => $"workflow-response-{workflowInstanceId}-pod";
}

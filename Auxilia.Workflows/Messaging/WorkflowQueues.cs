namespace Auxilia.Workflows.Messaging;

/// <summary>
/// Canonical queue-name conventions shared by the SDK and the Core.Runner so the
/// platform can pre-create an instance's response queue before the workflow starts.
/// </summary>
public static class WorkflowQueues
{
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
}

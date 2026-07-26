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
}

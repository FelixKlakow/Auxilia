namespace Auxilia.Workflows.Messaging;

/// <summary>
/// Canonical queue-name conventions shared by the SDK and the Steering Instance so the
/// platform can pre-create an instance's response queue before the workflow starts.
/// </summary>
public static class WorkflowQueues
{
    public static string ResponseQueueFor(Guid workflowInstanceId) => $"workflow-response-{workflowInstanceId}";
}

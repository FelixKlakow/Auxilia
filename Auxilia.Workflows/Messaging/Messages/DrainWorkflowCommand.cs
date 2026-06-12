namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Instructs a long-living workflow instance to finish in-flight work, accept no new
/// triggers, and shut down. Sent to <c>workflow-drain-{instanceId}</c> when its stored
/// configuration changes or a new version replaces it (drain-and-replace).
/// </summary>
public sealed record DrainWorkflowCommand(Guid WorkflowInstanceId);

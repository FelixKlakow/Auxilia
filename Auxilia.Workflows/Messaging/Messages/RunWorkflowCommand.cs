namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Sent by any top-level service (frontend, MCP server, automation API) to ask the
/// Steering Instance to launch a named workflow.
/// </summary>
public sealed record RunWorkflowCommand(
    /// <summary>Unique identifier for this dispatch request (used for logging and correlation).</summary>
    Guid CommandId,
    /// <summary>Workflow type name — must match a name recognised by the workflow image.</summary>
    string WorkflowType,
    /// <summary>Docker image to run (e.g. "auxilia-simple-git-workflow:latest").</summary>
    string WorkflowImage,
    /// <summary>
    /// Arbitrary key/value context forwarded to the workflow container as
    /// <c>WORKFLOW_CONTEXT__&lt;KEY&gt;</c> environment variables.
    /// </summary>
    IReadOnlyDictionary<string, string> Context);


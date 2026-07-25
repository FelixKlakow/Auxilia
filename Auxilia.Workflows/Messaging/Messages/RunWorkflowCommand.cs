namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Sent by any top-level service (frontend, MCP server, automation API) to ask the
/// Steering Instance to launch a named workflow.
/// </summary>
public sealed record RunWorkflowCommand(
    /// <summary>Unique identifier for this dispatch request (used for logging and correlation).</summary>
    Guid CommandId,
    /// <summary>
    /// Workflow type name — must match a name recognised by the workflow image. May be null
    /// when <see cref="WorkflowConfigurationId"/> is set; the configuration then supplies it.
    /// </summary>
    string? WorkflowType,
    /// <summary>
    /// URI of the signed workflow package (e.g. "https://packages.example.com/my-workflow.workflow.zip").
    /// May be null when <see cref="WorkflowConfigurationId"/> is set; the configuration then supplies it.
    /// </summary>
    string? WorkflowPackageUri,
    /// <summary>
    /// Arbitrary key/value context forwarded to the workflow container as
    /// <c>WORKFLOW_CONTEXT__&lt;KEY&gt;</c> environment variables.
    /// </summary>
    IReadOnlyDictionary<string, string> Context,
    /// <summary>
    /// Principal requesting the dispatch. The Policy Engine checks <c>workflow.trigger</c>
    /// for it during pre-flight. Null is accepted only while no authenticated entry points
    /// exist yet and when <c>WorkflowDispatcherSettings.RequirePrincipal</c> is false.
    /// </summary>
    Guid? RequestedBy = null,
    /// <summary>
    /// Optional named workflow configuration to dispatch from. When set, the dispatcher
    /// resolves workflow type, package URI, and slot bindings from the stored configuration;
    /// pre-flight fails the run when it is missing, disabled, or references unregistered
    /// slot providers.
    /// </summary>
    Guid? WorkflowConfigurationId = null,
    /// <summary>
    /// Run-scoped token the runner presents to Core.Api to resolve this run's slot credentials
    /// just-in-time. Minted at dispatch; carries no secret and is valid only for this run.
    /// </summary>
    string? ResolutionToken = null);

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Configuration for <see cref="WorkflowDispatcher"/>.
/// </summary>
public sealed class WorkflowDispatcherSettings
{
    public string CommandQueueName      { get; set; } = "workflow.run-commands";
    public string RegistrationQueueName { get; set; } = "workflow-registration";

    /// <summary>
    /// Queue this instance listens on for workflow announcements. Must be unique per Steering
    /// Instance when several share a broker: the launching instance holds the instance token,
    /// the pending package, and the slot configurations, so it must receive the announcement.
    /// The dispatcher injects this name into every launched workflow.
    /// </summary>
    public string AnnouncementQueueName { get; set; } = "workflow.announcements";

    /// <summary>
    /// When true (default), announcements and registrations must carry the one-time instance
    /// token issued at launch, and responses go only to the platform-created response queue.
    /// Disable only for trusted-operator dev scenarios where workflows start outside the dispatcher.
    /// </summary>
    public bool RequireInstanceToken { get; set; } = true;

    /// <summary>Maximum age of an issued instance token before registration is rejected.</summary>
    public TimeSpan InstanceTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// When true, RunWorkflowCommands without a <c>RequestedBy</c> principal are rejected.
    /// Default false until all entry points (dashboard, MCP, adapters) attach principals;
    /// commands that do carry a principal are always policy-checked regardless of this flag.
    /// </summary>
    public bool RequirePrincipal { get; set; }
}

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Configuration for <see cref="WorkflowDispatcher"/>.
/// </summary>
public sealed class WorkflowDispatcherSettings
{
    public string CommandQueueName      { get; set; } = "workflow.run-commands";
    public string RegistrationQueueName { get; set; } = "workflow-registration";
}

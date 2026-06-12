namespace Auxilia.BackendService.Dashboard;

/// <summary>Configuration for the dashboard UI, bound from the "Dashboard" section.</summary>
public sealed class DashboardSettings
{
    /// <summary>Queue the trigger page publishes <c>RunWorkflowCommand</c>s to.</summary>
    public string CommandQueueName { get; set; } = "workflow.run-commands";
}

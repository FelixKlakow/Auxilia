using Auxilia.Workflows;

namespace Auxilia.Workflows.TaskSource;

public static class TaskSourceWorkflowBuilderExtensions
{
    /// <summary>Full task-source access including status updates (<see cref="ITaskSourceAccess"/>).</summary>
    public static IWorkflowBuilder RequiresTaskSource(
        this IWorkflowBuilder builder,
        string name,
        TaskSourceCapabilities capabilities,
        string? description = null)
        => builder.Requires<ITaskSourceAccess>(name, capabilities, description);

    /// <summary>
    /// Read/comment work-item access (<see cref="IWorkItemAccess"/>) — for workflows that look
    /// items up and post comments but never change their status.
    /// </summary>
    public static IWorkflowBuilder RequiresWorkItems(
        this IWorkflowBuilder builder,
        string name,
        TaskSourceCapabilities capabilities,
        string? description = null)
        => builder.Requires<IWorkItemAccess>(name, capabilities, description);
}

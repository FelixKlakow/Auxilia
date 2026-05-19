using Auxilia.Workflows;

namespace Auxilia.Workflows.TaskSource;

public static class TaskSourceWorkflowBuilderExtensions
{
    public static IWorkflowBuilder RequiresTaskSource(
        this IWorkflowBuilder builder,
        string name,
        TaskSourceCapabilities capabilities,
        string? description = null)
        => builder.Requires(name, capabilities, description);
}

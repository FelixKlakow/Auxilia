using Auxilia.Workflows;

namespace Auxilia.Workflows.SourceControl;

public static class SourceControlWorkflowBuilderExtensions
{
    public static IWorkflowBuilder RequiresSourceControl(
        this IWorkflowBuilder builder,
        string name,
        SourceControlCapabilities capabilities,
        string? description = null)
        => builder.Requires(name, capabilities, description); // T inferred as SourceControlCapabilities
}

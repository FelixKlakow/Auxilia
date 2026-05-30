using Auxilia.Workflows;

namespace Auxilia.Workflows.AiAgent;

public static class AiAgentWorkflowBuilderExtensions
{
    public static IWorkflowBuilder RequiresAiAgent(
        this IWorkflowBuilder builder,
        string name,
        AiCapabilities capabilities,
        string? description = null)
        => builder.Requires<IAiAgent>(name, capabilities, description);
}

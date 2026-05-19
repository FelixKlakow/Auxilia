using Auxilia.Workflows;

namespace Auxilia.Workflows.PullRequestAccess;

public static class PullRequestAccessWorkflowBuilderExtensions
{
    public static IWorkflowBuilder RequiresPullRequestAccess(
        this IWorkflowBuilder builder,
        string name,
        PullRequestAccessCapabilities capabilities,
        string? description = null)
        => builder.Requires(name, capabilities, description);
}

using Auxilia.Workflows;

namespace Auxilia.Workflows.TestRunner;

public static class TestRunnerWorkflowBuilderExtensions
{
    public static IWorkflowBuilder RequiresTestRunner(
        this IWorkflowBuilder builder,
        string name,
        TestRunnerCapabilities capabilities,
        string? description = null)
        => builder.Requires(name, capabilities, description);
}

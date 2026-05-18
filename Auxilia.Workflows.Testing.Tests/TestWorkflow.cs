using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.Testing.Tests;

public static class TestWorkflow
{
    public static async Task RunAsync() =>
        await WorkflowBuilder.Create("test-workflow").Requires("db", new NoCapabilities()).Run(["--test-harness"]);
}

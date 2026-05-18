using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing;

namespace Auxilia.Workflows.Testing.Tests;

[TestFixture]
[Category("Component")]
public class TimeoutTests
{
    [Test]
    public void RunAsync_WhenNoResponseArrives_ThrowsWorkflowHarnessTimeoutException()
    {
        // Use an entry point that never announces so the harness times out on the announcement wait.
        var harness = WorkflowTestHarness
            .For(async () => await Task.Delay(TimeSpan.FromSeconds(5)))
            .WithDirective(WorkflowDirectiveKind.Run)
            .WithTimeout(TimeSpan.FromMilliseconds(50))
            .Build();

        Assert.ThrowsAsync<WorkflowHarnessTimeoutException>(async () => await harness.RunAsync());
    }
}

using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing;

namespace Auxilia.Workflows.Testing.Tests;

[TestFixture]
[Category("Component")]
public class RunFailedConfigTests
{
    [Test]
    public async Task Run_WhenConfigurationResponseIndicatesFailure_ReturnsResult_WithFailedState()
    {
        var harness = WorkflowTestHarness
            .For(TestWorkflow.RunAsync)
            .WithDirective(WorkflowDirectiveKind.Run)
            .Build();

        var result = await harness.RunAsync();

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed));
    }
}

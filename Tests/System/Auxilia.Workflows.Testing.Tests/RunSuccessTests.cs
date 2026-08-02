using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing;

namespace Auxilia.Workflows.Testing.Tests;

[TestFixture]
[Category("Component")]
public class RunSuccessTests
{
    [Test]
    public async Task Run_WithValidSlot_ReturnsResult_WithSuccessState()
    {
        var harness = WorkflowTestHarness
            .For(TestWorkflow.RunAsync)
            .WithDirective(WorkflowDirectiveKind.Run)
            .WithSlot("db", "fake-provider", new Dictionary<string, string>())
            .Build();

        var result = await harness.RunAsync();

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
    }
}

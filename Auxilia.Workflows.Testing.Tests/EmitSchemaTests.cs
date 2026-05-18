using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing;

namespace Auxilia.Workflows.Testing.Tests;

[TestFixture]
[Category("Component")]
public class EmitSchemaTests
{
    [Test]
    public async Task EmitSchema_RunAsync_ReturnsResult_WithCorrectSchema()
    {
        var harness = WorkflowTestHarness
            .For(TestWorkflow.RunAsync)
            .WithDirective(WorkflowDirectiveKind.EmitSchema)
            .Build();

        var result = await harness.RunAsync();

        Assert.That(result.Schema, Is.Not.Null);
        Assert.That(result.Schema!.WorkflowName, Is.EqualTo("test-workflow"));
        Assert.That(result.Schema.Slots, Has.Exactly(1).Matches<SlotDefinition>(s => s.SlotName == "db"));
    }
}

using Auxilia.Workflows;
using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.Testing.Tests;

[TestFixture]
[Category("Component")]
public class EmitSchemaTests
{
    private interface IStubService { }

    [Test]
    public void EmitSchema_RunAsync_ReturnsResult_WithCorrectSchema()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-workflow")
            .Requires<IStubService>("db", new NoCapabilities());

        var schema = builder.BuildSchema();

        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.WorkflowName, Is.EqualTo("test-workflow"));
        Assert.That(schema.Slots, Has.Exactly(1).Matches<SlotDefinition>(s => s.SlotName == "db"));
    }
}

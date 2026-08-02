using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing;

namespace Auxilia.Workflows.Testing.Tests;

[TestFixture]
[Category("Component")]
public class WorkflowHarnessPluginIntegrationTests
{
    [Test]
    public void TwoNamedAiSlots_SchemaContainsBothSlots()
    {
        var capabilities = new AiCapabilities
        {
            MinContextWindow = 8192,
            SupportedModalities = [Modality.Text]
        };
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("dual-slot-workflow")
            .RequiresAiAgent("primary-reviewer", capabilities)
            .RequiresAiAgent("secondary-reviewer", capabilities);

        var schema = builder.BuildSchema();

        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.Slots, Has.Count.EqualTo(2));
        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("primary-reviewer"));
        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("secondary-reviewer"));
        Assert.That(schema.Slots.Select(s => s.Capabilities),
            Has.All.InstanceOf<AiCapabilities>());
    }

    [Test]
    public void SlotNames_SurviveSchemaRoundTrip()
    {
        var capabilities = new AiCapabilities
        {
            MinContextWindow = 8192,
            SupportedModalities = [Modality.Text]
        };
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("dual-slot-workflow")
            .RequiresAiAgent("primary-reviewer", capabilities)
            .RequiresAiAgent("secondary-reviewer", capabilities);

        var schema = builder.BuildSchema();

        Assert.That(schema, Is.Not.Null);
        var slotNames = schema.Slots.Select(s => s.SlotName).ToList();
        Assert.That(slotNames, Does.Contain("primary-reviewer"));
        Assert.That(slotNames, Does.Contain("secondary-reviewer"));
        Assert.That(slotNames, Has.Count.EqualTo(2));
    }
}

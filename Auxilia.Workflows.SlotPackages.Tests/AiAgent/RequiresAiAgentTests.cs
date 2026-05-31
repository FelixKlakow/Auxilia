using Auxilia.Workflows.AiAgent;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
[Category("Unit")]
public class RequiresAiAgentTests
{
    [Test]
    public void RequiresAiAgent_AddsSlot_WithCorrectName()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresAiAgent("ai-agent", new AiCapabilities
            {
                MinContextWindow = 8192,
                SupportedModalities = [Modality.Text]
            });

        var schema = builder.BuildSchema();

        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.Slots, Has.Count.EqualTo(1));
        Assert.That(schema.Slots[0].SlotName, Is.EqualTo("ai-agent"));
        Assert.That(schema.Slots[0].ServiceType, Is.EqualTo(typeof(IAiAgent)));
    }
}


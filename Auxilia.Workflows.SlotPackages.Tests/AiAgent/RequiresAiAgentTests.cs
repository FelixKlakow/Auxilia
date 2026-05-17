using System.Text.Json;
using Auxilia.Workflows.AiAgent;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
public class RequiresAiAgentTests
{
    [Test]
    public async Task RequiresAiAgent_AddsSlot_WithCorrectName()
    {
        var writer = new StringWriter();
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresAiAgent("ai-agent", new AiCapabilities
        {
            MinContextWindow = 8192,
            SupportedModalities = [Modality.Text]
        });
        var wb = (WorkflowBuilder)builder;

        await wb.RunAsync(["schema"], writer, _ => { });

        var schema = JsonSerializer.Deserialize<WorkflowSchema>(writer.ToString());
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema!.Slots, Has.Count.EqualTo(1));
        Assert.That(schema.Slots[0].SlotName, Is.EqualTo("ai-agent"));
    }
}

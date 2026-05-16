using System.Text.Json;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.Workflows.SlotPackages.Tests.TaskSource;

[TestFixture]
public class RequiresTaskSourceTests
{
    [Test]
    public async Task RequiresTaskSource_RecordsSlot_InSchemaOutput()
    {
        var writer = new StringWriter();
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresTaskSource("task-source", new TaskSourceCapabilities
        {
            SupportedItemTypes = [ItemType.UserStory, ItemType.Bug]
        });
        var wb = (WorkflowBuilder)builder;

        await wb.RunAsync(["schema"], writer, _ => { });

        var schema = JsonSerializer.Deserialize<WorkflowSchema>(writer.ToString());
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema!.Slots, Has.Count.EqualTo(1));
        Assert.That(schema.Slots[0].SlotName, Is.EqualTo("task-source"));
    }

    [Test]
    public async Task RequiresTaskSource_ReturnsBuilder_ForFluency()
    {
        var builder = WorkflowBuilder.Create("test");
        var returned = builder.RequiresTaskSource("ts", new TaskSourceCapabilities
        {
            SupportedItemTypes = [ItemType.Feature]
        });
        Assert.That(returned, Is.SameAs(builder));
    }
}

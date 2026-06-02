using Auxilia.Workflows.TaskSource;

namespace Auxilia.Workflows.SlotPackages.Tests.TaskSource;

[TestFixture]
[Category("Unit")]
public class RequiresTaskSourceTests
{
    [Test]
    public void RequiresTaskSource_RecordsSlot_InSchemaOutput()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresTaskSource("task-source", new TaskSourceCapabilities
            {
                SupportedItemTypes = [ItemType.UserStory, ItemType.Bug]
            });

        var schema = builder.BuildSchema();

        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.Slots, Has.Count.EqualTo(1));
        Assert.That(schema.Slots[0].SlotName, Is.EqualTo("task-source"));
    }

    [Test]
    public void RequiresTaskSource_ReturnsBuilder_ForFluency()
    {
        var builder = WorkflowBuilder.Create("test");
        var returned = builder.RequiresTaskSource("ts", new TaskSourceCapabilities
        {
            SupportedItemTypes = [ItemType.Feature]
        });
        Assert.That(returned, Is.SameAs(builder));
    }
}

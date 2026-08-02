using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.SlotPackages.Tests.SourceControl;

[TestFixture]
[Category("Unit")]
public class RequiresSourceControlTests
{
    [Test]
    public void RequiresSourceControl_RecordsSlot_InSchemaOutput()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresSourceControl("sc-reader", new SourceControlCapabilities
            {
                RequiredPermissions = [Permission.Read]
            });

        var schema = builder.BuildSchema();

        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.Slots, Has.Count.EqualTo(1));
        Assert.That(schema.Slots[0].SlotName, Is.EqualTo("sc-reader"));
        Assert.That(schema.Slots[0].ServiceType, Is.EqualTo(typeof(ISourceControlAccess)));
    }

    [Test]
    public void RequiresSourceControl_TwoCalls_AccumulatesBothSlots()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test");
        builder
            .RequiresSourceControl("sc-reader", new SourceControlCapabilities { RequiredPermissions = [Permission.Read] })
            .RequiresSourceControl("sc-writer", new SourceControlCapabilities { RequiredPermissions = [Permission.Read, Permission.Write] });

        var schema = builder.BuildSchema();

        Assert.That(schema.Slots, Has.Count.EqualTo(2));
        Assert.That(schema.Slots.Select(s => s.SlotName), Is.EquivalentTo(new[] { "sc-reader", "sc-writer" }));
    }

    [Test]
    public void RequiresSourceControl_ReturnsBuilder_ForFluency()
    {
        var builder = WorkflowBuilder.Create("test");
        var returned = builder.RequiresSourceControl("sc", new SourceControlCapabilities
        {
            RequiredPermissions = [Permission.Read]
        });
        Assert.That(returned, Is.SameAs(builder));
    }
}

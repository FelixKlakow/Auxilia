using System.Linq;
using System.Text.Json;
using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.SlotPackages.Tests.SourceControl;

[TestFixture]
public class RequiresSourceControlTests
{
    [Test]
    public async Task RequiresSourceControl_RecordsSlot_InSchemaOutput()
    {
        var writer = new StringWriter();
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresSourceControl("sc-reader", new SourceControlCapabilities
        {
            RequiredPermissions = [Permission.Read]
        });
        var wb = (WorkflowBuilder)builder;

        await wb.RunAsync(["schema"], writer, _ => { });

        var schema = JsonSerializer.Deserialize<WorkflowSchema>(writer.ToString());
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema!.Slots, Has.Count.EqualTo(1));
        Assert.That(schema.Slots[0].SlotName, Is.EqualTo("sc-reader"));
    }

    [Test]
    public async Task RequiresSourceControl_TwoCalls_AccumulatesBothSlots()
    {
        var writer = new StringWriter();
        var builder = WorkflowBuilder.Create("test");
        builder
            .RequiresSourceControl("sc-reader", new SourceControlCapabilities { RequiredPermissions = [Permission.Read] })
            .RequiresSourceControl("sc-writer", new SourceControlCapabilities { RequiredPermissions = [Permission.Read, Permission.Write] });
        var wb = (WorkflowBuilder)builder;

        await wb.RunAsync(["schema"], writer, _ => { });

        var schema = JsonSerializer.Deserialize<WorkflowSchema>(writer.ToString());
        Assert.That(schema!.Slots, Has.Count.EqualTo(2));
        Assert.That(schema.Slots.Select(s => s.SlotName), Is.EquivalentTo(new[] { "sc-reader", "sc-writer" }));
    }

    [Test]
    public async Task RequiresSourceControl_ReturnsBuilder_ForFluency()
    {
        var builder = WorkflowBuilder.Create("test");
        var returned = builder.RequiresSourceControl("sc", new SourceControlCapabilities
        {
            RequiredPermissions = [Permission.Read]
        });
        Assert.That(returned, Is.SameAs(builder));
    }
}

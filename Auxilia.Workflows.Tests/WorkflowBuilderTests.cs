using System.Text;
using System.Text.Json;
using Auxilia.Workflows.Capabilities;
using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows.Tests;

[TestFixture]
public class WorkflowBuilderTests
{
    [Test]
    public void Create_ReturnsNonNull_IWorkflowBuilder()
    {
        var builder = WorkflowBuilder.Create("test");
        Assert.That(builder, Is.Not.Null);
    }

    [Test]
    public void FluentChaining_ReturnsSameBuilderInstance_ForEachMethod()
    {
        var builder = WorkflowBuilder.Create("test");
        var b1 = builder.Requires("slot1", new NoCapabilities());
        var b2 = b1.RequiresEnvironment(_ => { });
        var b3 = b2.WithMetadata(_ => { });
        Assert.That(b1, Is.SameAs(builder));
        Assert.That(b2, Is.SameAs(builder));
        Assert.That(b3, Is.SameAs(builder));
    }

    [Test]
    public async Task Requires_TwoCalls_ProducesTwoSlots_InSchemaOutput()
    {
        var writer = new StringWriter();
        var builder = WorkflowBuilder.Create("test");
        builder.Requires("slot-a", new NoCapabilities()).Requires("slot-b", new NoCapabilities());
        var wb = (WorkflowBuilder)builder;

        await wb.RunAsync(["schema"], writer, _ => { });

        var schema = JsonSerializer.Deserialize<WorkflowSchema>(writer.ToString());
        Assert.That(schema!.Slots, Has.Count.EqualTo(2));
    }

    [Test]
    public void Requires_DuplicateSlotName_ThrowsInvalidOperationException()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.Requires("slot-a", new NoCapabilities());
        Assert.Throws<InvalidOperationException>(() => builder.Requires("slot-a", new NoCapabilities()));
    }

    [Test]
    public async Task RequiresEnvironment_RequiresTool_AppendsToolRequirement()
    {
        var writer = new StringWriter();
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresEnvironment(e => e.RequireTool(Tool.Git));
        var wb = (WorkflowBuilder)builder;

        await wb.RunAsync(["schema"], writer, _ => { });

        var json = writer.ToString();
        Assert.That(json, Does.Contain("Git").Or.Contain("git"));
    }

    [Test]
    public async Task RequiresEnvironment_RequiresOs_AppendsOsRequirement()
    {
        var writer = new StringWriter();
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresEnvironment(e => e.RequireOs(OsConstraint.Linux));
        var wb = (WorkflowBuilder)builder;

        await wb.RunAsync(["schema"], writer, _ => { });

        var json = writer.ToString();
        Assert.That(json, Does.Contain("Linux").Or.Contain("linux"));
    }

    [Test]
    public async Task RequiresEnvironment_RequiresPort_AppendsPortRequirement()
    {
        var writer = new StringWriter();
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresEnvironment(e => e.RequirePort(8080));
        var wb = (WorkflowBuilder)builder;

        await wb.RunAsync(["schema"], writer, _ => { });

        var json = writer.ToString();
        Assert.That(json, Does.Contain("8080"));
    }

    [Test]
    public async Task RunAsync_SchemaMode_WritesValidJson_WithSchemaVersion1_0()
    {
        var writer = new StringWriter();
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("smoke");

        await wb.RunAsync(["schema"], writer, _ => { });

        var schema = JsonSerializer.Deserialize<WorkflowSchema>(writer.ToString());
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema!.SchemaVersion, Is.EqualTo("1.0"));
    }

    [Test]
    public async Task RunAsync_SchemaMode_WritesJson_ContainingAllDeclaredSlotNames()
    {
        var writer = new StringWriter();
        var builder = WorkflowBuilder.Create("workflow");
        builder.Requires("alpha", new NoCapabilities()).Requires("beta", new NoCapabilities());
        var wb = (WorkflowBuilder)builder;

        await wb.RunAsync(["schema"], writer, _ => { });

        var json = writer.ToString();
        Assert.That(json, Does.Contain("alpha"));
        Assert.That(json, Does.Contain("beta"));
    }

    [Test]
    public async Task RunAsync_SchemaMode_CallsExitAction_WithCode0()
    {
        var writer = new StringWriter();
        int capturedCode = -1;
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");

        await wb.RunAsync(["schema"], writer, code => capturedCode = code);

        Assert.That(capturedCode, Is.EqualTo(0));
    }

    [Test]
    public async Task RunAsync_EmptyArgs_TreatedAsSchemaMode()
    {
        var writer1 = new StringWriter();
        var writer2 = new StringWriter();
        var wb1 = (WorkflowBuilder)WorkflowBuilder.Create("test");
        var wb2 = (WorkflowBuilder)WorkflowBuilder.Create("test");

        await wb1.RunAsync(["schema"], writer1, _ => { });
        await wb2.RunAsync([], writer2, _ => { });

        Assert.That(writer2.ToString(), Is.EqualTo(writer1.ToString()));
    }
}

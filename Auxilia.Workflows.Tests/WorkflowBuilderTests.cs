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
    public void Requires_TwoCalls_ProducesTwoSlots_InSchema()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.Requires("slot-a", new NoCapabilities()).Requires("slot-b", new NoCapabilities());
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.Slots, Has.Count.EqualTo(2));
    }

    [Test]
    public void Requires_DuplicateSlotName_ThrowsInvalidOperationException()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.Requires("slot-a", new NoCapabilities());
        Assert.Throws<InvalidOperationException>(() => builder.Requires("slot-a", new NoCapabilities()));
    }

    [Test]
    public void RequiresEnvironment_RequiresTool_AppendsToolRequirement()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresEnvironment(e => e.RequiresTool("git"));
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.EnvironmentRequirements,
            Has.Some.InstanceOf<ToolRequirement>()
               .With.Property(nameof(ToolRequirement.ToolName)).EqualTo("git"));
    }

    [Test]
    public void RequiresEnvironment_RequiresOs_AppendsOsRequirement()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresEnvironment(e => e.RequiresOs(OsConstraint.Linux));
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.EnvironmentRequirements,
            Has.Some.InstanceOf<OsRequirement>()
               .With.Property(nameof(OsRequirement.Os)).EqualTo(OsConstraint.Linux));
    }

    [Test]
    public void RequiresEnvironment_RequiresPort_AppendsPortRequirement()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresEnvironment(e => e.RequiresPort(8080));
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.EnvironmentRequirements,
            Has.Some.InstanceOf<PortRequirement>()
               .With.Property(nameof(PortRequirement.Port)).EqualTo(8080));
    }

    [Test]
    public void BuildSchema_ReturnsSchema_WithSchemaVersion1_0()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("smoke");

        var schema = wb.BuildSchema();

        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.SchemaVersion, Is.EqualTo("1.0"));
    }

    [Test]
    public void BuildSchema_ContainsAllDeclaredSlotNames()
    {
        var builder = WorkflowBuilder.Create("workflow");
        builder.Requires("alpha", new NoCapabilities()).Requires("beta", new NoCapabilities());
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("alpha"));
        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("beta"));
    }

    [Test]
    public void BuildSchema_TwoCalls_ReturnSchemas_WithSameWorkflowNameAndVersion()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");

        var schema1 = wb.BuildSchema();
        var schema2 = wb.BuildSchema();

        Assert.That(schema2.WorkflowName, Is.EqualTo(schema1.WorkflowName));
        Assert.That(schema2.SchemaVersion, Is.EqualTo(schema1.SchemaVersion));
        Assert.That(schema2.Slots.Count, Is.EqualTo(schema1.Slots.Count));
    }
}

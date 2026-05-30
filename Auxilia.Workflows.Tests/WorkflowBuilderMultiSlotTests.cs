using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class WorkflowBuilderMultiSlotTests
{
    private static AiCapabilities DefaultAiCapabilities => new()
    {
        MinContextWindow = 8192,
        SupportedModalities = [Modality.Text]
    };

    private static SourceControlCapabilities DefaultScCapabilities => new()
    {
        RequiredPermissions = [Permission.Read]
    };

    [Test]
    public void TwoDistinctAiSlots_ProduceTwoSchemaEntries()
    {
        var builder = WorkflowBuilder.Create("multi-ai-test");
        builder
            .RequiresAiAgent("primary-reviewer", DefaultAiCapabilities)
            .RequiresAiAgent("secondary-reviewer", DefaultAiCapabilities);

        var schema = ((WorkflowBuilder)builder).BuildSchema();

        Assert.That(schema.Slots, Has.Count.EqualTo(2));
        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("primary-reviewer"));
        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("secondary-reviewer"));
        Assert.That(schema.Slots.Select(s => s.Capabilities),
            Has.All.InstanceOf<AiCapabilities>());
        Assert.That(schema.Slots[0].ServiceType, Is.EqualTo(typeof(IAiAgent)));
        Assert.That(schema.Slots[1].ServiceType, Is.EqualTo(typeof(IAiAgent)));
    }

    [Test]
    public void DuplicateAiSlotName_ThrowsInvalidOperationException()
    {
        var builder = WorkflowBuilder.Create("duplicate-ai-test");
        builder.RequiresAiAgent("primary-reviewer", DefaultAiCapabilities);

        Assert.Throws<InvalidOperationException>(() =>
            builder.RequiresAiAgent("primary-reviewer", DefaultAiCapabilities));
    }

    [Test]
    public void DuplicateSourceControlSlotName_ThrowsInvalidOperationException()
    {
        var builder = WorkflowBuilder.Create("duplicate-sc-test");
        builder.RequiresSourceControl("repo", DefaultScCapabilities);

        Assert.Throws<InvalidOperationException>(() =>
            builder.RequiresSourceControl("repo", DefaultScCapabilities));
    }
}

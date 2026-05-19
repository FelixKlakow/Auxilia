using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.SlotPackages.Tests.MultiSlot;

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
    public void RequiresAiAgent_AddsTwoSlots_WhenNamesAreDistinct()
    {
        var builder = global::Auxilia.Workflows.WorkflowBuilder.Create("multi-ai-test");
        builder
            .RequiresAiAgent("primary-reviewer", DefaultAiCapabilities)
            .RequiresAiAgent("secondary-reviewer", DefaultAiCapabilities);

        var wb = (global::Auxilia.Workflows.WorkflowBuilder)builder;
        var schema = wb.BuildSchema();

        Assert.That(schema.Slots, Has.Count.EqualTo(2));
        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("primary-reviewer"));
        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("secondary-reviewer"));
        Assert.That(schema.Slots.Select(s => s.Capabilities),
            Has.All.InstanceOf<AiCapabilities>());
    }

    [Test]
    public void RequiresAiAgent_ThrowsInvalidOperationException_WhenDuplicateNameUsed()
    {
        var builder = global::Auxilia.Workflows.WorkflowBuilder.Create("duplicate-ai-test");
        builder.RequiresAiAgent("primary-reviewer", DefaultAiCapabilities);

        Assert.Throws<InvalidOperationException>(() =>
            builder.RequiresAiAgent("primary-reviewer", DefaultAiCapabilities));
    }

    [Test]
    public void RequiresSourceControl_ThrowsInvalidOperationException_WhenDuplicateNameUsed()
    {
        var builder = global::Auxilia.Workflows.WorkflowBuilder.Create("duplicate-sc-test");
        builder.RequiresSourceControl("repo", DefaultScCapabilities);

        Assert.Throws<InvalidOperationException>(() =>
            builder.RequiresSourceControl("repo", DefaultScCapabilities));
    }
}

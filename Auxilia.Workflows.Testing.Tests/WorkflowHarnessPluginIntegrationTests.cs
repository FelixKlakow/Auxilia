using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing;

namespace Auxilia.Workflows.Testing.Tests;

[TestFixture]
[Category("Component")]
public class WorkflowHarnessPluginIntegrationTests
{
    private static async Task TwoAiSlotWorkflow()
    {
        var capabilities = new AiCapabilities
        {
            MinContextWindow = 8192,
            SupportedModalities = [Modality.Text]
        };
        await WorkflowBuilder.Create("dual-slot-workflow")
            .RequiresAiAgent("primary-reviewer", capabilities)
            .RequiresAiAgent("secondary-reviewer", capabilities)
            .Run(["--test-harness"]);
    }

    [Test]
    public async Task EmitSchema_TwoNamedAiSlots_SchemaContainsBothSlots()
    {
        var harness = WorkflowTestHarness
            .For(TwoAiSlotWorkflow)
            .WithDirective(WorkflowDirectiveKind.EmitSchema)
            .Build();

        var result = await harness.RunAsync();

        Assert.That(result.Schema, Is.Not.Null);
        Assert.That(result.Schema!.Slots, Has.Count.EqualTo(2));
        Assert.That(result.Schema.Slots.Select(s => s.SlotName), Does.Contain("primary-reviewer"));
        Assert.That(result.Schema.Slots.Select(s => s.SlotName), Does.Contain("secondary-reviewer"));
        Assert.That(result.Schema.Slots.Select(s => s.Capabilities),
            Has.All.InstanceOf<AiCapabilities>());
    }

    [Test]
    public async Task EmitSchema_SlotNames_SurviveHarnessRoundTrip()
    {
        var harness = WorkflowTestHarness
            .For(TwoAiSlotWorkflow)
            .WithDirective(WorkflowDirectiveKind.EmitSchema)
            .Build();

        var result = await harness.RunAsync();

        Assert.That(result.Schema, Is.Not.Null);
        var slotNames = result.Schema!.Slots.Select(s => s.SlotName).ToList();
        Assert.That(slotNames, Does.Contain("primary-reviewer"));
        Assert.That(slotNames, Does.Contain("secondary-reviewer"));
        Assert.That(slotNames, Has.Count.EqualTo(2));
    }
}

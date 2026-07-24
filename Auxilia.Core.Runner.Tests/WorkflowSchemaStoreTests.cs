using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Environment;

namespace Auxilia.Core.Runner.Tests;

[TestFixture]
public class WorkflowSchemaStoreTests
{
    private WorkflowSchemaStore _store = null!;

    [SetUp]
    public void SetUp() => _store = TestStores.NewWorkflowSchemaStore();

    [Test]
    public async Task GetSchema_AfterSet_ReturnsStoredSchema()
    {
        var schema = new WorkflowSchema("TestWorkflow", [], []);
        await _store.SetSchemaAsync("TestWorkflow", schema);

        var result = await _store.GetSchemaAsync("TestWorkflow");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.WorkflowName, Is.EqualTo("TestWorkflow"));
        Assert.That(result.Slots, Is.Empty);
    }

    [Test]
    public async Task GetSchema_MissingKey_ReturnsNull()
    {
        var result = await _store.GetSchemaAsync("NonExistent");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SetSchema_OverwritesExistingEntry()
    {
        var schemaV1 = new WorkflowSchema("TestWorkflow", [], []);
        var schemaV2 = new WorkflowSchema(
            "TestWorkflow",
            [new SlotDefinition("slot1", null) { ServiceType = typeof(object) }],
            []);

        await _store.SetSchemaAsync("TestWorkflow", schemaV1);
        await _store.SetSchemaAsync("TestWorkflow", schemaV2);

        var result = await _store.GetSchemaAsync("TestWorkflow");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Slots, Has.Count.EqualTo(1));
        Assert.That(result.Slots[0].SlotName, Is.EqualTo("slot1"));
    }
}

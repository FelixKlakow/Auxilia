using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Environment;

namespace Auxilia.SteeringInstance.Tests;

[TestFixture]
public class WorkflowSchemaStoreTests
{
    private WorkflowSchemaStore _store = null!;

    [SetUp]
    public void SetUp() => _store = new WorkflowSchemaStore();

    [Test]
    public void TryGetSchema_AfterSet_ReturnsStoredSchema()
    {
        var schema = new WorkflowSchema("TestWorkflow", [], []);
        _store.SetSchema("TestWorkflow", schema);

        var found = _store.TryGetSchema("TestWorkflow", out var result);

        Assert.That(found, Is.True);
        Assert.That(result, Is.EqualTo(schema));
    }

    [Test]
    public void TryGetSchema_MissingKey_ReturnsFalse()
    {
        var found = _store.TryGetSchema("NonExistent", out var result);

        Assert.That(found, Is.False);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void SetSchema_OverwritesExistingEntry()
    {
        var schemaV1 = new WorkflowSchema("TestWorkflow", [], []);
        var schemaV2 = new WorkflowSchema(
            "TestWorkflow",
            [new SlotDefinition("slot1", null) { ServiceType = typeof(object) }],
            []);

        _store.SetSchema("TestWorkflow", schemaV1);
        _store.SetSchema("TestWorkflow", schemaV2);

        _store.TryGetSchema("TestWorkflow", out var result);
        Assert.That(result, Is.EqualTo(schemaV2));
    }
}

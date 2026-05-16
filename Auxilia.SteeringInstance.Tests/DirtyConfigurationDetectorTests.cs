using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Environment;

namespace Auxilia.SteeringInstance.Tests;

[TestFixture]
public class DirtyConfigurationDetectorTests
{
    private WorkflowSchemaStore _schemaStore = null!;
    private SlotConfigurationStore _configStore = null!;
    private DirtyConfigurationDetector _detector = null!;

    [SetUp]
    public void SetUp()
    {
        _schemaStore = new WorkflowSchemaStore();
        _configStore = new SlotConfigurationStore();
        _detector = new DirtyConfigurationDetector(_schemaStore, _configStore);
    }

    [Test]
    public void Detect_FirstCall_StoresSchemaAndReturnsNotDirty()
    {
        var schema = MakeSchema("mySlot", new Caps(42));

        var result = _detector.Detect("TestWorkflow", schema);

        Assert.That(result.IsDirty, Is.False);
        Assert.That(result.AddedRequiredFieldsCount, Is.EqualTo(0));
        Assert.That(result.DirtyConfigurationCount, Is.EqualTo(0));

        // Schema was stored
        Assert.That(_schemaStore.TryGetSchema("TestWorkflow", out _), Is.True);
    }

    [Test]
    public void Detect_AddingOptionalField_ReturnsNotDirty()
    {
        // First registration stores the old schema
        _detector.Detect("TestWorkflow", MakeSchema("mySlot", new Caps(42)));

        // New schema adds a property with null value (optional)
        var updated = MakeSchema("mySlot", new CapsWithOptional(42, null));
        var result = _detector.Detect("TestWorkflow", updated);

        Assert.That(result.IsDirty, Is.False);
        Assert.That(result.AddedRequiredFieldsCount, Is.EqualTo(0));
    }

    [Test]
    public void Detect_AddingRequiredField_ReturnsDirtyWithCount()
    {
        // Seed a stored configuration so DirtyConfigurationCount can be > 0
        _configStore.UpsertConfiguration("TestWorkflow",
            new StoredSlotConfiguration("mySlot", "SomeProvider",
                new Dictionary<string, string>(), ConfigurationStatus.Valid));

        _detector.Detect("TestWorkflow", MakeSchema("mySlot", new Caps(42)));

        var updated = MakeSchema("mySlot", new CapsWithRequired(42, "value"));
        var result = _detector.Detect("TestWorkflow", updated);

        Assert.That(result.IsDirty, Is.True);
        Assert.That(result.AddedRequiredFieldsCount, Is.GreaterThan(0));
        Assert.That(result.DirtyConfigurationCount, Is.GreaterThan(0));
    }

    [Test]
    public void Detect_AddingRequiredField_CallsMarkDirty()
    {
        // Seed a stored configuration
        _configStore.UpsertConfiguration("TestWorkflow",
            new StoredSlotConfiguration("mySlot", "SomeProvider",
                new Dictionary<string, string>(), ConfigurationStatus.Valid));

        _detector.Detect("TestWorkflow", MakeSchema("mySlot", new Caps(42)));

        var updated = MakeSchema("mySlot", new CapsWithRequired(42, "value"));
        _detector.Detect("TestWorkflow", updated);

        var configs = _configStore.GetConfigurations("TestWorkflow");
        Assert.That(configs, Has.All.Property(nameof(StoredSlotConfiguration.Status))
            .EqualTo(ConfigurationStatus.Dirty));
    }

    // ---- helpers ----

    private static WorkflowSchema MakeSchema<T>(string slotName, T caps)
        => new("TestWorkflow", [new SlotDefinition(slotName, caps)], []);

    private sealed record Caps(int ExistingProp);
    private sealed record CapsWithOptional(int ExistingProp, string? OptionalProp);
    private sealed record CapsWithRequired(int ExistingProp, string NewRequiredProp);
}

using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Environment;

namespace Auxilia.Core.Runner.Tests;

[TestFixture]
public class DirtyConfigurationDetectorTests
{
    private WorkflowSchemaStore _schemaStore = null!;
    private SlotConfigurationStore _configStore = null!;
    private DirtyConfigurationDetector _detector = null!;

    [SetUp]
    public void SetUp()
    {
        _schemaStore = TestStores.NewWorkflowSchemaStore();
        _configStore = TestStores.NewSlotConfigurationStore();
        _detector = new DirtyConfigurationDetector(_schemaStore, _configStore);
    }

    [Test]
    public async Task Detect_FirstCall_StoresSchemaAndReturnsNotDirty()
    {
        var schema = MakeSchema("mySlot", new Caps(42));

        var result = await _detector.DetectAsync("TestWorkflow", schema);

        Assert.That(result.IsDirty, Is.False);
        Assert.That(result.AddedRequiredFieldsCount, Is.EqualTo(0));
        Assert.That(result.DirtyConfigurationCount, Is.EqualTo(0));

        // Schema was stored
        Assert.That(await _schemaStore.GetSchemaAsync("TestWorkflow"), Is.Not.Null);
    }

    [Test]
    public async Task Detect_AddingOptionalField_ReturnsNotDirty()
    {
        // First registration stores the old schema
        await _detector.DetectAsync("TestWorkflow", MakeSchema("mySlot", new Caps(42)));

        // New schema adds a property with null value (optional)
        var updated = MakeSchema("mySlot", new CapsWithOptional(42, null));
        var result = await _detector.DetectAsync("TestWorkflow", updated);

        Assert.That(result.IsDirty, Is.False);
        Assert.That(result.AddedRequiredFieldsCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Detect_AddingRequiredField_ReturnsDirtyWithCount()
    {
        // Seed a stored configuration so DirtyConfigurationCount can be > 0
        await _configStore.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("mySlot", "SomeProvider",
                new Dictionary<string, string>(), ConfigurationStatus.Valid));

        await _detector.DetectAsync("TestWorkflow", MakeSchema("mySlot", new Caps(42)));

        var updated = MakeSchema("mySlot", new CapsWithRequired(42, "value"));
        var result = await _detector.DetectAsync("TestWorkflow", updated);

        Assert.That(result.IsDirty, Is.True);
        Assert.That(result.AddedRequiredFieldsCount, Is.GreaterThan(0));
        Assert.That(result.DirtyConfigurationCount, Is.GreaterThan(0));
    }

    [Test]
    public async Task Detect_AddingRequiredField_CallsMarkDirty()
    {
        // Seed a stored configuration
        await _configStore.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("mySlot", "SomeProvider",
                new Dictionary<string, string>(), ConfigurationStatus.Valid));

        await _detector.DetectAsync("TestWorkflow", MakeSchema("mySlot", new Caps(42)));

        var updated = MakeSchema("mySlot", new CapsWithRequired(42, "value"));
        await _detector.DetectAsync("TestWorkflow", updated);

        var configs = await _configStore.GetConfigurationsAsync("TestWorkflow");
        Assert.That(configs, Has.All.Property(nameof(StoredSlotConfiguration.Status))
            .EqualTo(ConfigurationStatus.Dirty));
    }

    // ---- helpers ----

    private static WorkflowSchema MakeSchema<T>(string slotName, T caps)
        => new("TestWorkflow", [new SlotDefinition(slotName, caps) { ServiceType = typeof(object) }], []);

    private sealed record Caps(int ExistingProp);
    private sealed record CapsWithOptional(int ExistingProp, string? OptionalProp);
    private sealed record CapsWithRequired(int ExistingProp, string NewRequiredProp);
}

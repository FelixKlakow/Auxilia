using System.Text.Json;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.Workflows.SlotPackages.Tests.TaskSource;

[TestFixture]
public class TaskSourceCapabilitiesTests
{
    [Test]
    public void RoundTrip_KnownFields_Preserved()
    {
        var original = new TaskSourceCapabilities
        {
            SupportedItemTypes = [ItemType.UserStory, ItemType.Bug, ItemType.Feature, ItemType.Epic]
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<TaskSourceCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.SupportedItemTypes, Is.EqualTo(original.SupportedItemTypes));
    }

    [Test]
    public void RoundTrip_ExtensionData_Preserved()
    {
        var json = """{"SupportedItemTypes":["UserStory"],"unknownField":"someValue"}""";

        var deserialized = JsonSerializer.Deserialize<TaskSourceCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Extensions, Is.Not.Null);
        Assert.That(deserialized.Extensions!.ContainsKey("unknownField"), Is.True);
        Assert.That(deserialized.Extensions["unknownField"].GetString(), Is.EqualTo("someValue"));

        var reserialised = JsonSerializer.Serialize(deserialized);
        Assert.That(reserialised, Does.Contain("unknownField"));
        Assert.That(reserialised, Does.Contain("someValue"));
    }

    [Test]
    public void ItemType_EnumValues_SerialiseToNameStrings()
    {
        Assert.That(JsonSerializer.Serialize(ItemType.UserStory), Is.EqualTo(@"""UserStory"""));
        Assert.That(JsonSerializer.Serialize(ItemType.Bug), Is.EqualTo(@"""Bug"""));
        Assert.That(JsonSerializer.Serialize(ItemType.Feature), Is.EqualTo(@"""Feature"""));
        Assert.That(JsonSerializer.Serialize(ItemType.Epic), Is.EqualTo(@"""Epic"""));
    }
}

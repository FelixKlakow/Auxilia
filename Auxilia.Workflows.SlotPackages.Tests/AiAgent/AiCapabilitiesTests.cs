using System.Text.Json;
using Auxilia.Workflows.AiAgent;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
[Category("Unit")]
public class AiCapabilitiesTests
{
    [Test]
    public void RoundTrip_RequiredFields_SerialiseCorrectly()
    {
        var original = new AiCapabilities
        {
            MinContextWindow = 8192,
            SupportedModalities = [Modality.Text, Modality.Image]
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AiCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.MinContextWindow, Is.EqualTo(8192));
        Assert.That(deserialized.SupportedModalities, Is.EqualTo(new[] { Modality.Text, Modality.Image }));
    }

    [Test]
    public void RoundTrip_NullableOptionalField_Preserved()
    {
        var withValue = new AiCapabilities
        {
            MinContextWindow = 4096,
            SupportedModalities = [Modality.Text],
            MaxOutputTokens = 1024
        };

        var json = JsonSerializer.Serialize(withValue);
        var deserialized = JsonSerializer.Deserialize<AiCapabilities>(json);

        Assert.That(deserialized!.MaxOutputTokens, Is.EqualTo(1024));

        var withNull = new AiCapabilities
        {
            MinContextWindow = 4096,
            SupportedModalities = [Modality.Text],
            MaxOutputTokens = null
        };

        var json2 = JsonSerializer.Serialize(withNull);
        var deserialized2 = JsonSerializer.Deserialize<AiCapabilities>(json2);

        Assert.That(deserialized2!.MaxOutputTokens, Is.Null);
    }

    [Test]
    public void RoundTrip_ExtensionData_Preserved()
    {
        var json = """{"MinContextWindow":2048,"SupportedModalities":["Text"],"customField":"hello"}""";

        var deserialized = JsonSerializer.Deserialize<AiCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Extensions, Is.Not.Null);
        Assert.That(deserialized.Extensions!.ContainsKey("customField"), Is.True);
        Assert.That(deserialized.Extensions["customField"].GetString(), Is.EqualTo("hello"));

        var reserialised = JsonSerializer.Serialize(deserialized);
        Assert.That(reserialised, Does.Contain("customField"));
        Assert.That(reserialised, Does.Contain("hello"));
    }
}

using System.Text.Json;
using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.Tests;

/// <summary>
/// Wire-format guarantees of the view contracts: descriptors round-trip through the
/// registration ViewsJson (including the optional RendererKey) and chat entries round-trip
/// through view item payloads.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ViewContractSerializationTests
{
    [Test]
    public void ViewDescriptor_WithRendererKey_RoundTripsThroughJson()
    {
        var descriptor = new ViewDescriptor(
            "agent-conversation", "{}", ViewRendering.Custom, ViewLifecycle.LiveAndPersisted, "agent-chat");

        var json = JsonSerializer.Serialize(new List<ViewDescriptor> { descriptor });
        var restored = JsonSerializer.Deserialize<List<ViewDescriptor>>(json)!;

        Assert.That(restored.Single(), Is.EqualTo(descriptor));
        Assert.That(restored.Single().RendererKey, Is.EqualTo("agent-chat"));
    }

    [Test]
    public void ViewDescriptor_WithoutRendererKey_SerializesAndStaysNull()
    {
        var descriptor = new ViewDescriptor("progress", "{}", ViewRendering.Log, ViewLifecycle.Persisted);

        var restored = JsonSerializer.Deserialize<ViewDescriptor>(JsonSerializer.Serialize(descriptor))!;

        Assert.That(restored.RendererKey, Is.Null);
    }

    [Test]
    public void ViewDescriptor_LegacyJsonWithoutRendererKey_DeserializesWithNullKey()
    {
        // ViewsJson written before the RendererKey field existed must keep deserializing.
        const string legacyJson =
            """{"Name":"progress","ItemSchemaJson":"{}","Rendering":1,"Lifecycle":2}""";

        var restored = JsonSerializer.Deserialize<ViewDescriptor>(legacyJson)!;

        Assert.That(restored.Name, Is.EqualTo("progress"));
        Assert.That(restored.RendererKey, Is.Null);
    }

    [Test]
    public void AgentChatEntry_AllFields_RoundTripThroughJson()
    {
        var entry = new AgentChatEntry(
            AgentChatRole.Tool, "// file content", DateTimeOffset.Parse("2026-06-12T08:00:00Z"),
            Label: "src/Widget.cs", ToolName: "read_file", ToolState: "Success");

        var restored = JsonSerializer.Deserialize<AgentChatEntry>(JsonSerializer.Serialize(entry));

        Assert.That(restored, Is.EqualTo(entry));
    }

    [Test]
    public void AgentChatEntry_OptionalFieldsOmitted_DefaultToNull()
    {
        var entry = new AgentChatEntry(AgentChatRole.User, "Hello", DateTimeOffset.UtcNow);

        var restored = JsonSerializer.Deserialize<AgentChatEntry>(JsonSerializer.Serialize(entry))!;

        Assert.Multiple(() =>
        {
            Assert.That(restored.Label, Is.Null);
            Assert.That(restored.ToolName, Is.Null);
            Assert.That(restored.ToolState, Is.Null);
        });
    }
}

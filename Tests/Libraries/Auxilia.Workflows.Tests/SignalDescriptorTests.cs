using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Workflows;
using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class SignalDescriptorTests
{
    private sealed record SamplePayload(string Name, int Value);

    [Test]
    public void DeclaresSignal_OneCall_PropagatesSignalDescriptorToManifestAndSchema()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-wf");
        builder.DeclaresSignal<SamplePayload>("my-signal", "A test signal");

        var schema = builder.BuildSchema();
        var manifest = builder.BuildManifest();

        Assert.That(schema.Signals, Has.Count.EqualTo(1));
        Assert.That(schema.Signals[0].Name, Is.EqualTo("my-signal"));
        Assert.That(schema.Signals[0].Description, Is.EqualTo("A test signal"));

        Assert.That(manifest.Signals, Has.Count.EqualTo(1));
        Assert.That(manifest.Signals[0].Name, Is.EqualTo("my-signal"));
        Assert.That(manifest.Signals[0].Description, Is.EqualTo("A test signal"));
    }

    [Test]
    public void DeclaresSignal_NoCall_BothArtifactsHaveEmptySignals()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-wf");

        Assert.That(builder.BuildSchema().Signals, Is.Empty);
        Assert.That(builder.BuildManifest().Signals, Is.Empty);
    }

    [Test]
    public void DeclaresSignal_DuplicateSignalName_ThrowsInvalidOperationException()
    {
        var builder = WorkflowBuilder.Create("test-wf");
        builder.DeclaresSignal<SamplePayload>("dup-signal");

        Assert.Throws<InvalidOperationException>(() => builder.DeclaresSignal<SamplePayload>("dup-signal"));
    }

    [Test]
    public void DeclaresSignal_FluentChain_ReturnsSameBuilderInstance()
    {
        var builder = WorkflowBuilder.Create("test-wf");
        var result = builder.DeclaresSignal<SamplePayload>("sig-a");

        Assert.That(result, Is.SameAs(builder));
    }

    [Test]
    public void DeclaresSignal_PayloadSchema_IsNonEmptyJsonString()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test-wf");
        builder.DeclaresSignal<SamplePayload>("schema-signal");

        var descriptor = builder.BuildSchema().Signals[0];

        Assert.That(descriptor.PayloadSchema, Is.Not.Null.And.Not.Empty);

        // Should be parseable as JSON
        Assert.DoesNotThrow(() => JsonDocument.Parse(descriptor.PayloadSchema));
    }

    [Test]
    public void WorkflowConfigurationResponse_WithSignalHandlers_SerializesAndDeserializesRoundTrip()
    {
        var handlers = new Dictionary<string, Auxilia.Workflows.Messaging.Messages.ISignalHandlerDescriptor>
        {
            ["signal-a"] = new Auxilia.Workflows.Messaging.Messages.InvokeWorkflowSignalHandler("target-wf"),
            ["signal-b"] = new Auxilia.Workflows.Messaging.Messages.NotifySignalHandler("email", new Dictionary<string, string> { ["to"] = "a@b.com" }),
            ["signal-c"] = new Auxilia.Workflows.Messaging.Messages.NullSignalHandler()
        };

        var response = new Auxilia.Workflows.Messaging.Messages.WorkflowConfigurationResponse(
            Guid.NewGuid(), true, null,
            new Dictionary<string, Auxilia.Workflows.Messaging.Messages.EncryptedSlotConfiguration>(),
            handlers);

        var options = new JsonSerializerOptions { WriteIndented = false };
        var json = JsonSerializer.Serialize(response, options);
        var deserialized = JsonSerializer.Deserialize<Auxilia.Workflows.Messaging.Messages.WorkflowConfigurationResponse>(json, options);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.SignalHandlers, Has.Count.EqualTo(3));
        Assert.That(deserialized.SignalHandlers["signal-a"], Is.InstanceOf<Auxilia.Workflows.Messaging.Messages.InvokeWorkflowSignalHandler>());
        Assert.That(deserialized.SignalHandlers["signal-b"], Is.InstanceOf<Auxilia.Workflows.Messaging.Messages.NotifySignalHandler>());
        Assert.That(deserialized.SignalHandlers["signal-c"], Is.InstanceOf<Auxilia.Workflows.Messaging.Messages.NullSignalHandler>());
    }

    [Test]
    public void InvokeWorkflowSignalHandler_Discriminator_DeserializesToCorrectConcreteType()
    {
        var original = new Auxilia.Workflows.Messaging.Messages.InvokeWorkflowSignalHandler("my-target-wf");
        var json = JsonSerializer.Serialize<Auxilia.Workflows.Messaging.Messages.ISignalHandlerDescriptor>(original);
        var result = JsonSerializer.Deserialize<Auxilia.Workflows.Messaging.Messages.ISignalHandlerDescriptor>(json);

        Assert.That(result, Is.InstanceOf<Auxilia.Workflows.Messaging.Messages.InvokeWorkflowSignalHandler>());
        Assert.That(((Auxilia.Workflows.Messaging.Messages.InvokeWorkflowSignalHandler)result!).TargetWorkflowName, Is.EqualTo("my-target-wf"));
    }
}

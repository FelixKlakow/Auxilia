using System.Text.Json;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class SettingDescriptorTests
{
    [Test]
    public void Serialize_AllFields_RoundTripsLosslessly()
    {
        var descriptor = new SettingDescriptor(
            Key: "Folder",
            Label: "Mail folder",
            Kind: SettingKind.Choice,
            Required: true,
            HelpText: "Folder that is polled.",
            DefaultValue: "INBOX",
            Choices: ["INBOX", "Archive"]);

        var roundTripped = JsonSerializer.Deserialize<SettingDescriptor>(
            JsonSerializer.Serialize(descriptor))!;

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped.Key, Is.EqualTo("Folder"));
            Assert.That(roundTripped.Label, Is.EqualTo("Mail folder"));
            Assert.That(roundTripped.Kind, Is.EqualTo(SettingKind.Choice));
            Assert.That(roundTripped.Required, Is.True);
            Assert.That(roundTripped.HelpText, Is.EqualTo("Folder that is polled."));
            Assert.That(roundTripped.DefaultValue, Is.EqualTo("INBOX"));
            Assert.That(roundTripped.Choices, Is.EqualTo(new[] { "INBOX", "Archive" }));
        });
    }

    [Test]
    public void Serialize_Kind_IsAMachineReadableString()
    {
        var json = JsonSerializer.Serialize(
            new SettingDescriptor("Password", "Password", SettingKind.Secret));

        Assert.That(json, Does.Contain("\"Kind\":\"Secret\""));
    }

    [Test]
    public void Deserialize_OptionalFieldsAbsent_FallBackToDefaults()
    {
        var descriptor = JsonSerializer.Deserialize<SettingDescriptor>(
            """{ "Key": "ImapHost", "Label": "IMAP host", "Kind": "Text" }""")!;

        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Required, Is.False);
            Assert.That(descriptor.HelpText, Is.Null);
            Assert.That(descriptor.DefaultValue, Is.Null);
            Assert.That(descriptor.Choices, Is.Null);
        });
    }

    [Test]
    public void PluginManifest_WithSettings_RoundTripsLosslessly()
    {
        var manifest = new PluginManifest("email-work-items", "aGFzaA==", "c2ln", "cHVi",
        [
            new SettingDescriptor("ImapHost", "IMAP host", SettingKind.Text, Required: true),
            new SettingDescriptor("ImapPort", "IMAP port", SettingKind.Number, DefaultValue: "3143")
        ]);

        var roundTripped = JsonSerializer.Deserialize<PluginManifest>(
            JsonSerializer.Serialize(manifest))!;

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped.ProviderType, Is.EqualTo("email-work-items"));
            Assert.That(roundTripped.Settings, Has.Count.EqualTo(2));
            Assert.That(roundTripped.Settings![0],
                Is.EqualTo(new SettingDescriptor("ImapHost", "IMAP host", SettingKind.Text, Required: true)));
            Assert.That(roundTripped.Settings[1].DefaultValue, Is.EqualTo("3143"));
        });
    }

    [Test]
    public void PluginManifest_LegacyJsonWithoutSettings_DeserializesWithNullSettings()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            """
            {
              "ProviderType": "fake-provider",
              "ContentHashBase64": "",
              "SignatureBase64": "",
              "PublicKeyBase64": ""
            }
            """)!;

        Assert.Multiple(() =>
        {
            Assert.That(manifest.ProviderType, Is.EqualTo("fake-provider"));
            Assert.That(manifest.Settings, Is.Null);
        });
    }
}

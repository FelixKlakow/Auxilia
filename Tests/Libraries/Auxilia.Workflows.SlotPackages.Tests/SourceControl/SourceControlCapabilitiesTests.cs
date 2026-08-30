using System.Text.Json;
using Auxilia.Workflows;
using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.SlotPackages.Tests.SourceControl;

[TestFixture]
[Category("Unit")]
public class SourceControlCapabilitiesTests
{
    [Test]
    public void RoundTrip_RequiredFields_Preserved()
    {
        var original = new SourceControlCapabilities
        {
            RequiredPermissions = [Permission.Read, Permission.Write],
            SupportedHostTypes = [SourceHostTypes.GitHub, SourceHostTypes.GitLab, SourceHostTypes.AzureDevOps]
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SourceControlCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.RequiredPermissions, Is.EqualTo(original.RequiredPermissions));
        Assert.That(deserialized.SupportedHostTypes, Is.EqualTo(original.SupportedHostTypes));
    }

    [Test]
    public void RoundTrip_NullableSupportedHostTypes_RoundTrips()
    {
        var original = new SourceControlCapabilities
        {
            RequiredPermissions = [Permission.Read],
            SupportedHostTypes = null
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SourceControlCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.SupportedHostTypes, Is.Null);
    }

    [Test]
    public void RoundTrip_ExtensionData_Preserved()
    {
        var json = """{"RequiredPermissions":["Read"],"unknownField":"extraValue"}""";

        var deserialized = JsonSerializer.Deserialize<SourceControlCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Extensions, Is.Not.Null);
        Assert.That(deserialized.Extensions!.ContainsKey("unknownField"), Is.True);
        Assert.That(deserialized.Extensions["unknownField"].GetString(), Is.EqualTo("extraValue"));

        var reserialised = JsonSerializer.Serialize(deserialized);
        Assert.That(reserialised, Does.Contain("unknownField"));
        Assert.That(reserialised, Does.Contain("extraValue"));
    }

    [Test]
    public void Permission_EnumValues_Exist()
    {
        Assert.That(JsonSerializer.Serialize(Permission.Read), Is.EqualTo(@"""Read"""));
        Assert.That(JsonSerializer.Serialize(Permission.Write), Is.EqualTo(@"""Write"""));
    }

    [Test]
    public void RoundTrip_UnknownHostTypeString_Preserved()
    {
        // Host types are an OPEN vocabulary — a value the SDK has never heard of must survive.
        var original = new SourceControlCapabilities
        {
            RequiredPermissions = [Permission.Read],
            SupportedHostTypes = ["some-future-host"]
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SourceControlCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.SupportedHostTypes, Is.EqualTo(new[] { "some-future-host" }));
    }

    [Test]
    public void WellKnownHostTypes_AreLowercaseStrings()
    {
        Assert.That(SourceHostTypes.GitHub, Is.EqualTo("github"));
        Assert.That(SourceHostTypes.GitLab, Is.EqualTo("gitlab"));
        Assert.That(SourceHostTypes.AzureDevOps, Is.EqualTo("azure-devops"));
    }
}

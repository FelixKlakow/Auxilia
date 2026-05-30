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
            SupportedHostTypes = [SourceHostType.GitHub, SourceHostType.GitLab]
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
    public void SourceHostType_EnumValues_SerializeAsStrings()
    {
        Assert.That(JsonSerializer.Serialize(SourceHostType.GitHub), Is.EqualTo(@"""GitHub"""));
        Assert.That(JsonSerializer.Serialize(SourceHostType.GitLab), Is.EqualTo(@"""GitLab"""));
    }
}

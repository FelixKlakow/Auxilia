using System.Text.Json;
using Auxilia.Workflows;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.Workflows.SlotPackages.Tests.PullRequestAccess;

[TestFixture]
[Category("Unit")]
public class PullRequestAccessCapabilitiesTests
{
    [Test]
    public void RoundTrip_RequiredPermissions_Typed()
    {
        var original = new PullRequestAccessCapabilities
        {
            RequiredPermissions = [PullRequestPermission.Read, PullRequestPermission.Write]
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<PullRequestAccessCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.RequiredPermissions, Is.EqualTo(original.RequiredPermissions));
    }

    [Test]
    public void Permission_EnumValues_SerializeAsStrings()
    {
        Assert.That(JsonSerializer.Serialize(PullRequestPermission.Read), Is.EqualTo(@"""Read"""));
        Assert.That(JsonSerializer.Serialize(PullRequestPermission.Write), Is.EqualTo(@"""Write"""));
    }

    [Test]
    public void RoundTrip_ExtensionData_Preserved()
    {
        var json = """{"RequiredPermissions":["Read"],"unknownField":"extraValue"}""";

        var deserialized = JsonSerializer.Deserialize<PullRequestAccessCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Extensions, Is.Not.Null);
        Assert.That(deserialized.Extensions!.ContainsKey("unknownField"), Is.True);
        Assert.That(deserialized.Extensions["unknownField"].GetString(), Is.EqualTo("extraValue"));

        var reserialised = JsonSerializer.Serialize(deserialized);
        Assert.That(reserialised, Does.Contain("unknownField"));
        Assert.That(reserialised, Does.Contain("extraValue"));
    }

    [Test]
    public void RoundTrip_PrHostType_Typed()
    {
        var original = new PullRequestAccessCapabilities
        {
            RequiredPermissions = [PullRequestPermission.Read],
            PrHostType = SourceHostType.GitHub
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<PullRequestAccessCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.PrHostType, Is.EqualTo(SourceHostType.GitHub));
    }

    [Test]
    public void RoundTrip_NullablePrHostType_RoundTrips()
    {
        var original = new PullRequestAccessCapabilities
        {
            RequiredPermissions = [PullRequestPermission.Read],
            PrHostType = null
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<PullRequestAccessCapabilities>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.PrHostType, Is.Null);
    }
}

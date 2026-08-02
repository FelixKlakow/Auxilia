using Auxilia.Core.Api;
using Auxilia.Core.Contracts;
using Auxilia.Governance.IdentityImport;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>The Core.Api wire mapping between the identity-import contracts and the Governance model.</summary>
[TestFixture]
[Category("Unit")]
public sealed class IdentityImportMappingTests
{
    [Test]
    public void ToDraft_CarriesEveryField_ForCreateAndEdit()
    {
        var request = new SaveIdentitySourceRequest(
            Name: "corp-ad",
            ConnectorType: "ldap",
            Settings: new Dictionary<string, string> { ["Host"] = "ad.corp", ["BindPassword"] = "pw" },
            DefaultRole: "User",
            GroupRoleMappings: new Dictionary<string, string> { ["admins"] = "Operator" },
            DisableMissing: true,
            ExistingName: "corp-ad");

        var draft = request.ToDraft();

        Assert.Multiple(() =>
        {
            Assert.That(draft.ExistingName, Is.EqualTo("corp-ad"));
            Assert.That(draft.Name, Is.EqualTo("corp-ad"));
            Assert.That(draft.ConnectorType, Is.EqualTo("ldap"));
            Assert.That(draft.DefaultRole, Is.EqualTo("User"));
            Assert.That(draft.DisableMissing, Is.True);
            Assert.That(draft.Settings["Host"], Is.EqualTo("ad.corp"));
            Assert.That(draft.Settings["BindPassword"], Is.EqualTo("pw"));
            Assert.That(draft.GroupRoleMappings["admins"], Is.EqualTo("Operator"));
        });
    }

    [Test]
    public void ToDto_ProjectsView_IncludingStoredSecretKeysAndSummary()
    {
        var id = Guid.NewGuid();
        var view = new IdentitySourceView(
            id, "corp-ad", "ldap", DisableMissing: true, DefaultRole: "User",
            GroupRoleMappings: new Dictionary<string, string> { ["admins"] = "Operator" },
            Settings: new Dictionary<string, string> { ["Host"] = "ad.corp", ["BindPassword"] = "" },
            StoredSecretKeys: new HashSet<string> { "BindPassword" },
            LastImport: new IdentityImportSummary(2, 1, 0, 3, ["skipped row 4"]),
            LastImportUtc: DateTimeOffset.UnixEpoch);

        var dto = view.ToDto();

        Assert.Multiple(() =>
        {
            Assert.That(dto.Id, Is.EqualTo(id));
            Assert.That(dto.DisableMissing, Is.True);
            Assert.That(dto.Settings["BindPassword"], Is.Empty, "secret values are withheld");
            Assert.That(dto.StoredSecretKeys, Is.EquivalentTo(new[] { "BindPassword" }));
            Assert.That(dto.GroupRoleMappings["admins"], Is.EqualTo("Operator"));
            Assert.That(dto.LastImport!.Created, Is.EqualTo(2));
            Assert.That(dto.LastImport.Updated, Is.EqualTo(1));
            Assert.That(dto.LastImport.Skipped, Is.EqualTo(3));
            Assert.That(dto.LastImport.Warnings, Is.EquivalentTo(new[] { "skipped row 4" }));
            Assert.That(dto.LastImportUtc, Is.EqualTo(DateTimeOffset.UnixEpoch));
        });
    }

    [Test]
    public void ToDescriptorDto_ExposesConnectorSettings()
    {
        var dto = new CsvIdentityImportConnector().ToDescriptorDto();

        Assert.Multiple(() =>
        {
            Assert.That(dto.ConnectorType, Is.EqualTo("csv"));
            Assert.That(dto.Settings, Has.One.Matches<IdentityConnectorSettingDto>(
                s => s.Key == "Csv" && s.Required && s.Multiline));
        });
    }
}

using Auxilia.Governance.IdentityImport;

namespace Auxilia.Governance.Tests.UnitTests.IdentityImport;

/// <summary>
/// Unit-testable parts of the LDAP connector: settings → request mapping with defaults,
/// descriptor declarations, and group-DN extraction. The protocol call itself is covered
/// by the IdentityImport system tests.
/// </summary>
[TestFixture]
[Category("Unit")]
public class LdapIdentityImportConnectorTests
{
    private static Dictionary<string, string> MinimalSettings() => new()
    {
        ["Host"] = "ldap.example.org",
        ["BindDn"] = "cn=admin,dc=example,dc=org",
        ["BindPassword"] = "secret-bind-pw",
        ["BaseDn"] = "ou=people,dc=example,dc=org"
    };

    [Test]
    public void Parse_AppliesDocumentedDefaults()
    {
        var settings = LdapImportSettings.Parse(MinimalSettings());

        Assert.Multiple(() =>
        {
            Assert.That(settings.Port, Is.EqualTo(389));
            Assert.That(settings.UseSsl, Is.False);
            Assert.That(settings.UserFilter, Is.EqualTo("(objectClass=person)"));
            Assert.That(settings.UsernameAttribute, Is.EqualTo("uid"));
            Assert.That(settings.DisplayNameAttribute, Is.EqualTo("cn"));
            Assert.That(settings.GroupAttribute, Is.EqualTo("memberOf"));
        });
    }

    [Test]
    public void Parse_HonoursExplicitValues()
    {
        var raw = MinimalSettings();
        raw["Port"] = "636";
        raw["UseSsl"] = "true";
        raw["UserFilter"] = "(objectClass=user)";
        raw["UsernameAttribute"] = "sAMAccountName";
        raw["GroupAttribute"] = "";

        var settings = LdapImportSettings.Parse(raw);

        Assert.Multiple(() =>
        {
            Assert.That(settings.Port, Is.EqualTo(636));
            Assert.That(settings.UseSsl, Is.True);
            Assert.That(settings.UserFilter, Is.EqualTo("(objectClass=user)"));
            Assert.That(settings.UsernameAttribute, Is.EqualTo("sAMAccountName"));
            Assert.That(settings.GroupAttribute, Is.Empty);
        });
    }

    [TestCase("Host")]
    [TestCase("BindDn")]
    [TestCase("BindPassword")]
    [TestCase("BaseDn")]
    public void Parse_RequiresConnectionSettings(string key)
    {
        var raw = MinimalSettings();
        raw.Remove(key);
        var ex = Assert.Throws<ArgumentException>(() => LdapImportSettings.Parse(raw));
        Assert.That(ex!.Message, Does.Contain(key));
    }

    [Test]
    public void Parse_RejectsNonNumericPort()
        => Assert.Throws<ArgumentException>(() =>
            LdapImportSettings.Parse(new Dictionary<string, string>(MinimalSettings()) { ["Port"] = "ldap" }));

    [Test]
    public void RequestedAttributes_IncludeGroupAttribute_OnlyWhenConfigured()
    {
        var withGroups = LdapImportSettings.Parse(MinimalSettings());
        Assert.That(withGroups.RequestedAttributes,
            Is.EquivalentTo(new[] { "uid", "cn", "userAccountControl", "memberOf" }));

        var withoutGroups = LdapImportSettings.Parse(
            new Dictionary<string, string>(MinimalSettings()) { ["GroupAttribute"] = "" });
        Assert.That(withoutGroups.RequestedAttributes,
            Is.EquivalentTo(new[] { "uid", "cn", "userAccountControl" }));
    }

    [TestCase("cn=Reviewers,ou=groups,dc=example,dc=org", "Reviewers")]
    [TestCase("CN=Domain Admins,CN=Users,DC=corp,DC=local", "Domain Admins")]
    [TestCase("plain-group-name", "plain-group-name")]
    public void GroupNameOf_ExtractsFirstRdnValue_OrKeepsPlainValues(string raw, string expected)
        => Assert.That(LdapIdentityImportConnector.GroupNameOf(raw), Is.EqualTo(expected));

    [Test]
    public void Descriptors_MarkBindPasswordAsSecret_AndMentionActiveDirectoryUsernameAttribute()
    {
        var connector = new LdapIdentityImportConnector();
        var byKey = connector.SettingDescriptors.ToDictionary(d => d.Key);

        Assert.Multiple(() =>
        {
            Assert.That(byKey["BindPassword"].Kind, Is.EqualTo(ConnectorSettingKind.Secret));
            Assert.That(byKey["Port"].DefaultValue, Is.EqualTo("389"));
            Assert.That(byKey["UserFilter"].DefaultValue, Is.EqualTo("(objectClass=person)"));
            Assert.That(byKey["UsernameAttribute"].DefaultValue, Is.EqualTo("uid"));
            Assert.That(byKey["UsernameAttribute"].HelpText, Does.Contain("sAMAccountName"));
            Assert.That(byKey["DisplayNameAttribute"].DefaultValue, Is.EqualTo("cn"));
        });
    }

    [Test]
    public async Task TestConnection_UnreachableHost_ReportsFailureWithoutSecrets()
    {
        var connector = new LdapIdentityImportConnector();
        var raw = MinimalSettings();
        raw["Host"] = "127.0.0.1";
        raw["Port"] = "1"; // nothing listens here

        var result = await connector.TestConnectionAsync(raw);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Not.Contain("secret-bind-pw"),
                "connection errors must never leak the bind password");
        });
    }
}

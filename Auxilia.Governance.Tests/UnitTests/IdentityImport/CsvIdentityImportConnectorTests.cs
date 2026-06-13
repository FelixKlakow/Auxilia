using Auxilia.Governance.IdentityImport;

namespace Auxilia.Governance.Tests.UnitTests.IdentityImport;

[TestFixture]
[Category("Unit")]
public class CsvIdentityImportConnectorTests
{
    private readonly CsvIdentityImportConnector _connector = new();

    private static Dictionary<string, string> Settings(string csv) => new() { ["Csv"] = csv };

    [Test]
    public async Task FetchUsers_ParsesPlainRows_WithDefaults()
    {
        var fetch = await _connector.FetchUsersAsync(Settings(
            """
            u-1,ada,Ada Lovelace,true,reviewers;admins
            u-2,grace
            """));

        Assert.Multiple(() =>
        {
            Assert.That(fetch.Users, Has.Count.EqualTo(2));
            Assert.That(fetch.SkippedEntries, Is.Empty);
        });
        var ada = fetch.Users[0];
        Assert.Multiple(() =>
        {
            Assert.That(ada.ExternalId, Is.EqualTo("u-1"));
            Assert.That(ada.Username, Is.EqualTo("ada"));
            Assert.That(ada.DisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(ada.Enabled, Is.True);
            Assert.That(ada.Groups, Is.EqualTo(new[] { "reviewers", "admins" }));
            // Missing optional columns: display name falls back to username, enabled defaults true.
            Assert.That(fetch.Users[1].DisplayName, Is.EqualTo("grace"));
            Assert.That(fetch.Users[1].Enabled, Is.True);
            Assert.That(fetch.Users[1].Groups, Is.Empty);
        });
    }

    [Test]
    public async Task FetchUsers_SupportsQuotedFields_WithCommasAndEscapedQuotes()
    {
        var fetch = await _connector.FetchUsersAsync(Settings(
            "u-1,ada,\"Lovelace, Ada \"\"The First\"\"\",false,\"reviewers\""));

        var ada = fetch.Users.Single();
        Assert.Multiple(() =>
        {
            Assert.That(ada.DisplayName, Is.EqualTo("Lovelace, Ada \"The First\""));
            Assert.That(ada.Enabled, Is.False);
            Assert.That(ada.Groups, Is.EqualTo(new[] { "reviewers" }));
        });
    }

    [Test]
    public async Task FetchUsers_SkipsHeaderRow_AndBlankLines()
    {
        var fetch = await _connector.FetchUsersAsync(Settings(
            "externalId,username,displayName,enabled,groups\n\nu-1,ada\n"));

        Assert.Multiple(() =>
        {
            Assert.That(fetch.Users, Has.Count.EqualTo(1));
            Assert.That(fetch.SkippedEntries, Is.Empty);
        });
    }

    [Test]
    public async Task FetchUsers_SkipsAndReportsBadRows_KeepsGoodOnes()
    {
        var fetch = await _connector.FetchUsersAsync(Settings(
            """
            u-1,ada
            only-one-column
            ,missing-external-id
            u-2,grace,Grace,maybe
            u-3,linus,"unterminated quote
            u-4,mary
            """));

        Assert.Multiple(() =>
        {
            Assert.That(fetch.Users.Select(u => u.Username), Is.EqualTo(new[] { "ada", "mary" }));
            Assert.That(fetch.SkippedEntries, Has.Count.EqualTo(4));
            Assert.That(fetch.SkippedEntries[0], Does.Contain("line 2"));
            Assert.That(fetch.SkippedEntries[1], Does.Contain("line 3"));
            Assert.That(fetch.SkippedEntries[2], Does.Contain("line 4").And.Contain("maybe"));
            Assert.That(fetch.SkippedEntries[3], Does.Contain("line 5").And.Contain("unterminated"));
        });
    }

    [TestCase("true", true)]
    [TestCase("1", true)]
    [TestCase("YES", true)]
    [TestCase("false", false)]
    [TestCase("0", false)]
    [TestCase("No", false)]
    public async Task FetchUsers_ParsesEnabledVariants(string value, bool expected)
    {
        var fetch = await _connector.FetchUsersAsync(Settings($"u-1,ada,Ada,{value}"));
        Assert.That(fetch.Users.Single().Enabled, Is.EqualTo(expected));
    }

    [Test]
    public async Task TestConnection_ReportsParsedAndSkippedCounts()
    {
        var ok = await _connector.TestConnectionAsync(Settings("u-1,ada\nbad-row"));
        Assert.Multiple(() =>
        {
            Assert.That(ok.Success, Is.True);
            Assert.That(ok.Message, Does.Contain("1 user(s) parsed").And.Contain("1 row(s) skipped"));
        });

        var empty = await _connector.TestConnectionAsync(Settings(""));
        Assert.That(empty.Success, Is.False);
    }

    [Test]
    public void Descriptors_DeclareOneRequiredMultilineTextSetting()
    {
        var descriptor = _connector.SettingDescriptors.Single();
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Key, Is.EqualTo("Csv"));
            Assert.That(descriptor.Kind, Is.EqualTo(ConnectorSettingKind.Text));
            Assert.That(descriptor.Required, Is.True);
            Assert.That(descriptor.Multiline, Is.True);
        });
    }
}

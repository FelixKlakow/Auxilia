using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Bulk/offline identity provisioning through the Core (LDAP/AD/CSV) — the Core owns identity.
/// Drives the surface via the typed <see cref="ICoreClient"/>: CSV import upserts principals into
/// the Core identity store idempotently, secrets never round-trip, and the endpoints are gated by
/// the identity-source-management permission. MCP parity is over the same service layer.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class IdentityImportAdminTests : CoreApiComponentTestBase
{
    private const string Csv =
        "externalId,username,displayName,enabled,groups\n" +
        "ada,ada,Ada Lovelace,true,reviewers\n" +
        "grace,grace,Grace Hopper,true,reviewers\n" +
        "linus,linus,Linus Torvalds,true,";

    private static SaveIdentitySourceRequest CsvSource(string name) => new(
        Name: name,
        ConnectorType: "csv",
        Settings: new Dictionary<string, string> { ["Csv"] = Csv },
        DefaultRole: "User",
        GroupRoleMappings: new Dictionary<string, string> { ["reviewers"] = "Operator" },
        DisableMissing: false);

    [Test]
    public async Task CsvImport_UpsertsPrincipalsIntoCoreStore_Idempotently()
    {
        ICoreClient core = new CoreClient(CreateClient());

        var source = await core.SaveIdentitySourceAsync(CsvSource("csv-" + Guid.NewGuid().ToString("N")));

        // First import: three principals created; role mappings applied.
        var first = await core.ImportIdentitySourceAsync(source.Id);
        Assert.Multiple(() =>
        {
            Assert.That(first.Created, Is.EqualTo(3), string.Join("; ", first.Warnings));
            Assert.That(first.Updated, Is.EqualTo(0));
            Assert.That(first.Disabled, Is.EqualTo(0));
        });

        // The principals really landed in the Core identity store.
        var principals = Factory.Services.GetRequiredService<IDataAccess<PrincipalRecord>>();
        var prefix = $"identity-source:{source.Id:D}:";
        var provisioned = (await principals.ReadAsync())
            .Where(p => p.ExternalSubject != null && p.ExternalSubject.StartsWith(prefix))
            .ToList();
        Assert.That(provisioned, Has.Count.EqualTo(3));
        Assert.That(provisioned.All(p => p.Status == "Active"), Is.True);

        // Re-import is idempotent: nothing created/updated, no duplicates.
        var second = await core.ImportIdentitySourceAsync(source.Id);
        Assert.Multiple(() =>
        {
            Assert.That(second.Created, Is.EqualTo(0));
            Assert.That(second.Updated, Is.EqualTo(0));
            Assert.That(second.Skipped, Is.EqualTo(3));
        });

        var afterReimport = (await principals.ReadAsync())
            .Count(p => p.ExternalSubject != null && p.ExternalSubject.StartsWith(prefix));
        Assert.That(afterReimport, Is.EqualTo(3), "re-import must not create duplicates");
    }

    [Test]
    public async Task ListGetDelete_RoundTripsThroughTheTypedClient()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var name = "csv-" + Guid.NewGuid().ToString("N");
        var saved = await core.SaveIdentitySourceAsync(CsvSource(name));

        var listed = await core.ListIdentitySourcesAsync();
        Assert.That(listed.Select(s => s.Id), Does.Contain(saved.Id));

        var fetched = await core.GetIdentitySourceAsync(saved.Id);
        Assert.That(fetched, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(fetched!.Name, Is.EqualTo(name));
            Assert.That(fetched.ConnectorType, Is.EqualTo("csv"));
            Assert.That(fetched.GroupRoleMappings["reviewers"], Is.EqualTo("Operator"));
        });

        await core.DeleteIdentitySourceAsync(saved.Id);

        Assert.Multiple(async () =>
        {
            Assert.That(await core.GetIdentitySourceAsync(saved.Id), Is.Null);
            Assert.That((await core.ListIdentitySourcesAsync()).Select(s => s.Id),
                Does.Not.Contain(saved.Id));
        });
    }

    [Test]
    public async Task TestConnection_OnCsvSource_ReportsUserCount()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var source = await core.SaveIdentitySourceAsync(CsvSource("csv-" + Guid.NewGuid().ToString("N")));

        var result = await core.TestIdentitySourceAsync(source.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.Contain("3 user(s)"));
        });
    }

    [Test]
    public async Task SaveThenGet_LdapSource_NeverReturnsTheBindPassword()
    {
        var admin = CreateClient();
        var request = new SaveIdentitySourceRequest(
            Name: "ldap-" + Guid.NewGuid().ToString("N"),
            ConnectorType: "ldap",
            Settings: new Dictionary<string, string>
            {
                ["Host"] = "ldap.example.test",
                ["BindDn"] = "cn=admin,dc=example,dc=test",
                ["BindPassword"] = "sup3r-s3cret-bind-pw",
                ["BaseDn"] = "ou=people,dc=example,dc=test"
            },
            DefaultRole: "User",
            GroupRoleMappings: new Dictionary<string, string>(),
            DisableMissing: false);

        var created = await admin.PostAsJsonAsync("/api/identity/sources", request);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var saved = await created.Content.ReadFromJsonAsync<IdentitySourceDto>();
        var raw = await (await admin.GetAsync($"/api/identity/sources/{saved!.Id}")).Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(raw, Does.Not.Contain("sup3r-s3cret-bind-pw"), "the bind password must never leave the Core");
            Assert.That(saved.StoredSecretKeys, Does.Contain("BindPassword"), "a stored secret is reported as present, value withheld");
        });
    }

    [Test]
    public async Task Save_WithUnknownConnectorType_Returns400()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/identity/sources", new
        {
            name = "bad-" + Guid.NewGuid().ToString("N"),
            connectorType = "smoke-signals",
            settings = new Dictionary<string, string>(),
            defaultRole = "User",
            groupRoleMappings = new Dictionary<string, string>(),
            disableMissing = false
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Import_UnknownSource_Returns404()
    {
        var response = await CreateClient().PostAsync($"/api/identity/sources/{Guid.NewGuid()}/import", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Anonymous_IsRejected()
    {
        var response = await CreateAnonymousClient().GetAsync("/api/identity/sources");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}

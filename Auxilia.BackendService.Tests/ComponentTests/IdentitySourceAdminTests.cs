using Auxilia.Governance;
using Auxilia.Governance.IdentityImport;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Identity-source administration (#23) through the real DI container: sources are
/// policy-gated to administrators, mutations and imports are audited, and the import path
/// (the page's "Import now" action) runs against a fake connector registered in DI.
/// </summary>
[TestFixture]
[Category("Component")]
public class IdentitySourceAdminTests : DashboardComponentTestBase
{
    private sealed class FakeConnector : IIdentityImportConnector
    {
        public string ConnectorType => "component-fake";
        public string DisplayName => "Component fake";
        public string Description => "Test double";

        public IReadOnlyList<ConnectorSettingDescriptor> SettingDescriptors { get; } =
        [
            new("Endpoint", "Endpoint", ConnectorSettingKind.Text, Required: true),
            new("Token", "Token", ConnectorSettingKind.Secret, Required: true)
        ];

        public List<ExternalUser> Users { get; } = [];

        public Task<ConnectorTestResult> TestConnectionAsync(
            IReadOnlyDictionary<string, string> settings, CancellationToken ct = default)
            => Task.FromResult(new ConnectorTestResult(true, $"{Users.Count} user(s)"));

        public Task<IdentityImportFetch> FetchUsersAsync(
            IReadOnlyDictionary<string, string> settings, CancellationToken ct = default)
            => Task.FromResult(new IdentityImportFetch(Users.ToList(), []));
    }

    private readonly FakeConnector _fakeConnector = new();

    protected override void ConfigureTestServices(IServiceCollection services)
        => services.AddSingleton<IIdentityImportConnector>(_fakeConnector);

    private IdentityImportService ImportService
        => Factory.Services.GetRequiredService<IdentityImportService>();

    private IdentitySourceDraft NewDraft(string name)
    {
        var draft = new IdentitySourceDraft { Name = name, ConnectorType = "component-fake" };
        draft.Settings["Endpoint"] = "https://idp.example.org";
        draft.Settings["Token"] = "component-secret";
        return draft;
    }

    [Test]
    public async Task ManageAction_IsGrantedToAdministratorsOnly()
    {
        using var client = CreateClient();
        var (_, adminId) = await LoginAsync(client, AdminUsername, AdminPassword);
        var policyEngine = Factory.Services.GetRequiredService<IPolicyEngine>();

        var admin = await policyEngine.EvaluateAsync(new PolicyContext(
            adminId, PermissionActions.IdentitySourceManage, "identity-sources"));
        Assert.That(admin.Allowed, Is.True);

        foreach (var role in new[] { "Operator", "User", "Auditor" })
        {
            var (principalId, _, _) = await CreatePrincipalAsync(role);
            var decision = await policyEngine.EvaluateAsync(new PolicyContext(
                principalId, PermissionActions.IdentitySourceManage, "identity-sources"));
            Assert.That(decision.Allowed, Is.False, $"{role} must not manage identity sources");
        }
    }

    [Test]
    public async Task CreateSource_IsAudited_AndStoresSettingsProtected()
    {
        using var client = CreateClient();
        var (_, adminId) = await LoginAsync(client, AdminUsername, AdminPassword);

        await ImportService.SaveAsync(adminId.ToString("D"), NewDraft("audited-source"));

        var records = await Factory.Services.GetRequiredService<IDataAccess<IdentitySourceRecord>>().ReadAsync();
        var record = records.Single(r => r.Name == "audited-source");
        Assert.Multiple(() =>
        {
            Assert.That(record.ProtectedSettingsJson, Does.StartWith("enc1:"),
                "the component host runs with AES-GCM protection — settings must be encrypted");
            Assert.That(record.ProtectedSettingsJson, Does.Not.Contain("component-secret"));
        });

        var audit = await Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>().ReadAsync();
        Assert.That(audit.Any(a =>
                a.Action == "identity-source.saved" &&
                a.Subject == "audited-source" &&
                a.Actor == adminId.ToString("D") &&
                a.Outcome == "created"),
            Is.True, "creating a source must be audited");
    }

    [Test]
    public async Task ImportNow_WithFakeConnector_CreatesPrincipals_AndRendersSummaryOnPage()
    {
        using var client = CreateClient();
        var (cookie, adminId) = await LoginAsync(client, AdminUsername, AdminPassword);

        var source = await ImportService.SaveAsync(adminId.ToString("D"), NewDraft("import-source"));
        _fakeConnector.Users.Clear();
        _fakeConnector.Users.Add(new ExternalUser("u-1", "Ada Lovelace", "ada", true, ["reviewers"]));
        _fakeConnector.Users.Add(new ExternalUser("u-2", "Grace Hopper", "grace", true, []));

        // The same service call the page's "Import now" button runs.
        var summary = await ImportService.ImportAsync(adminId.ToString("D"), source.Id);
        Assert.Multiple(() =>
        {
            Assert.That(summary.Created, Is.EqualTo(2));
            Assert.That(summary.Disabled, Is.EqualTo(0));
        });

        var principals = await Factory.Services.GetRequiredService<IDataAccess<PrincipalRecord>>().ReadAsync();
        Assert.That(principals.Count(p =>
                p.ExternalSubject != null &&
                p.ExternalSubject.StartsWith($"identity-source:{source.Id:D}:")),
            Is.EqualTo(2));

        var audit = await Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>().ReadAsync();
        Assert.That(audit.Any(a => a.Action == "identity-import.run" && a.Subject == "import-source"),
            Is.True, "the import run must be audited");

        // The page renders the persisted summary for the source row.
        var html = await GetHtmlAsync(client, "/admin/identity-sources", cookie);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("import-source"));
            Assert.That(html, Does.Contain("2 created"));
            Assert.That(html, Does.Not.Contain("component-secret"), "secrets never reach the page");
        });
    }

    [Test]
    public async Task Page_AsAdministrator_ShowsConnectorChooserAndCreateForm()
    {
        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var html = await GetHtmlAsync(client, "/admin/identity-sources", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Identity sources"));
            Assert.That(html, Does.Contain("LDAP / Active Directory"));
            Assert.That(html, Does.Contain("CSV upload"));
            Assert.That(html, Does.Contain("Imported users sign in once an administrator sets credentials"));
            Assert.That(html, Does.Not.Contain("Access denied"));
        });
    }

    [Test]
    public async Task Page_AsOperator_IsDeniedByPolicy()
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, "/admin/identity-sources", cookie);

        Assert.That(html, Does.Contain("Access denied"));
    }

    [Test]
    public async Task DeleteSource_IsAudited()
    {
        using var client = CreateClient();
        var (_, adminId) = await LoginAsync(client, AdminUsername, AdminPassword);
        var source = await ImportService.SaveAsync(adminId.ToString("D"), NewDraft("doomed-source"));

        Assert.That(await ImportService.DeleteAsync(adminId.ToString("D"), source.Id), Is.True);

        var audit = await Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>().ReadAsync();
        Assert.That(audit.Any(a => a.Action == "identity-source.deleted" && a.Subject == "doomed-source"),
            Is.True);
    }
}

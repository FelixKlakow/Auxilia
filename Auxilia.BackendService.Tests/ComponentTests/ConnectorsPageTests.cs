using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// The connectors page: signed-in accounts, own personal vs company; foreign personal
/// connectors are visible to administrators only, and tokens never render.
/// </summary>
[TestFixture]
[Category("Component")]
public class ConnectorsPageTests : DashboardComponentTestBase
{
    private async Task SeedAsync(string name, string displayName, string scope, Guid? owner = null)
    {
        var records = Factory.Services.GetRequiredService<IDataAccess<ConnectorRecord>>();
        var protector = Factory.Services.GetRequiredService<ISettingsProtector>();
        await records.SaveAsync(new ConnectorRecord
        {
            Id = ConnectorRecord.IdFor(name),
            Name = name,
            DisplayName = displayName,
            FlowKey = "anthropic-claude",
            ProtectedToken = protector.Protect("connector-token-value"),
            Scope = scope,
            OwnerPrincipalId = owner
        });
    }

    [Test]
    public async Task ConnectorsPage_AsUser_ShowsOwnAndCompany_ButNoForeignPersonal_AndNoToken()
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("User");
        var (cookie, principalId) = await LoginAsync(client, username, password);

        await SeedAsync($"own-claude-{principalId:N}", "My Claude account",
            SlotInstanceScope.Personal, owner: principalId);
        await SeedAsync($"company-github-{principalId:N}", "Company GitHub bot",
            SlotInstanceScope.Company);
        await SeedAsync($"foreign-claude-{principalId:N}", "Someone elses Claude",
            SlotInstanceScope.Personal, owner: Guid.NewGuid());

        var html = await GetHtmlAsync(client, "/connectors", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("My Claude account"));
            Assert.That(html, Does.Contain("Company GitHub bot"), "company connectors are visible to everyone");
            Assert.That(html, Does.Not.Contain("Someone elses Claude"), "foreign personal connectors stay hidden");
            Assert.That(html, Does.Contain("Connect an account"));
            Assert.That(html, Does.Not.Contain("connector-token-value"), "credentials must never render");
            Assert.That(html, Does.Not.Contain("Access denied"));
        });
    }

    [Test]
    public async Task ConnectorsPage_AsOperator_AlsoShowsForeignPersonalConnectors()
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, principalId) = await LoginAsync(client, username, password);

        await SeedAsync($"managed-claude-{principalId:N}", "Managed Claude seat",
            SlotInstanceScope.Personal, owner: Guid.NewGuid());

        var html = await GetHtmlAsync(client, "/connectors", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Managed Claude seat"),
                "administrators manage everyone's connectors here");
            Assert.That(html, Does.Not.Contain("Access denied"));
        });
    }
}

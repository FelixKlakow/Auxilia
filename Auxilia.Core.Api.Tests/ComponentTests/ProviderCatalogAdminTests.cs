using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The administrator surface for the Core-owned slot-provider catalog (REST): deny-by-default
/// availability, per-setting curation, the provider-catalog.manage auth gate, and that catalog
/// state does not gate run dispatch (it is a configuration-time allowlist). MCP parity is covered
/// by the same <see cref="Auxilia.Core.Api.Mcp.CoreMcpTools"/> service layer these endpoints call.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ProviderCatalogAdminTests : CoreApiComponentTestBase
{
    private static readonly SettingDescriptor[] EmailDescriptors =
    [
        new("ImapHost", "IMAP host", SettingKind.Text, Required: true),
        new("Password", "Password", SettingKind.Secret, Required: true)
    ];

    private async Task SeedProviderAsync(
        string providerType, string dllPath = null!, IReadOnlyList<SettingDescriptor>? descriptors = null)
    {
        var store = Factory.Services.GetRequiredService<IDataAccess<SlotProviderRecord>>();
        await store.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor(providerType),
            ProviderType = providerType,
            DllPath = dllPath ?? $"/plugins/{providerType}.slothandler.dll",
            SettingDescriptorsJson = descriptors is null ? null : JsonSerializer.Serialize(descriptors)
        });
    }

    private async Task<HttpClient> ClientForRolesAsync(params string[] roles)
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}", "Service");
        foreach (var role in roles.Where(BuiltInRoles.Exists))
            await directory.AssignRoleAsync(principal.Id, role);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    [Test]
    public async Task List_ProviderWithoutCuration_IsDenyByDefaultUnavailable()
    {
        await SeedProviderAsync("email-work-items", descriptors: EmailDescriptors);

        var page = await CreateClient().GetFromJsonAsync<PagedResult<ProviderCatalogEntry>>("/api/provider-catalog");

        Assert.That(page!.Items, Has.Count.EqualTo(1));
        Assert.That(page.Items[0].Available, Is.False, "availability is deny-by-default");
    }

    [Test]
    public async Task SetAvailability_ThenList_ShowsAvailable()
    {
        await SeedProviderAsync("email-work-items", descriptors: EmailDescriptors);
        var admin = CreateClient();

        var set = await admin.PostAsJsonAsync(
            "/api/provider-catalog/email-work-items/availability", new SetProviderAvailability(true));
        Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var entry = await set.Content.ReadFromJsonAsync<ProviderCatalogEntry>();
        Assert.That(entry!.Available, Is.True);

        var page = await admin.GetFromJsonAsync<PagedResult<ProviderCatalogEntry>>("/api/provider-catalog?available=true");
        Assert.That(page!.Items.Single().ProviderType, Is.EqualTo("email-work-items"));
    }

    [Test]
    public async Task SetSetting_Disabled_MarksTheDescriptor()
    {
        await SeedProviderAsync("email-work-items", descriptors: EmailDescriptors);

        var response = await CreateClient().PostAsJsonAsync(
            "/api/provider-catalog/email-work-items/settings", new SetProviderSetting("Password", true));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var entry = await response.Content.ReadFromJsonAsync<ProviderCatalogEntry>();
        Assert.That(entry!.Descriptors.Single(d => d.Key == "Password").Disabled, Is.True);
    }

    [Test]
    public async Task SetAvailability_UnknownProvider_Returns404()
    {
        var response = await CreateClient().PostAsJsonAsync(
            "/api/provider-catalog/ghost/availability", new SetProviderAvailability(true));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task SetSetting_UnknownKey_Returns400()
    {
        await SeedProviderAsync("email-work-items", descriptors: EmailDescriptors);

        var response = await CreateClient().PostAsJsonAsync(
            "/api/provider-catalog/email-work-items/settings", new SetProviderSetting("NoSuchKey", true));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.Auditor, HttpStatusCode.Forbidden)]
    public async Task ProviderCatalog_IsAdministratorOnly(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.GetAsync("/api/provider-catalog");
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task Anonymous_IsRejected()
    {
        var response = await CreateAnonymousClient().GetAsync("/api/provider-catalog");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task UnavailableProvider_DoesNotGateRunDispatch()
    {
        // The catalog is a configuration-time allowlist, not a dispatch gate: a run that binds a
        // hidden (deny-by-default) provider still dispatches — matching the migrated semantics.
        await SeedProviderAsync("email-work-items", descriptors: EmailDescriptors);

        var response = await CreateClient().PostAsJsonAsync("/api/runs", new
        {
            workflowType = "wt",
            packageUri = "docker://img",
            slotBindings = new[] { new { slotName = "src", providerType = "email-work-items" } }
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "provider availability must not reject a run — it only gates configuration editors");
    }
}

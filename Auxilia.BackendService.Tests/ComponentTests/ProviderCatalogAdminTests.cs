using System.Text.Json;
using Auxilia.BackendService.Dashboard;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Provider-catalog curation (#19) through the real DI container: availability toggles are
/// policy-gated to administrators and audited, and the editor read path serves the
/// manifest descriptors with admin overrides merged in.
/// </summary>
[TestFixture]
[Category("Component")]
public class ProviderCatalogAdminTests : DashboardComponentTestBase
{
    private ProviderCatalogService Catalog
        => Factory.Services.GetRequiredService<ProviderCatalogService>();

    private async Task SeedEmailProviderAsync()
    {
        var providers = Factory.Services.GetRequiredService<IDataAccess<SlotProviderRecord>>();
        await providers.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor("email-work-items"),
            ProviderType = "email-work-items",
            DllPath = "/plugins/Auxilia.Slots.Email.slothandler.dll",
            SettingDescriptorsJson = JsonSerializer.Serialize(new List<SettingDescriptor>
            {
                new("ImapHost", "IMAP host", SettingKind.Text, Required: true),
                new("Password", "Password", SettingKind.Secret, Required: true),
                new("Folder", "Mail folder", SettingKind.Text, DefaultValue: "INBOX")
            })
        });
    }

    [Test]
    public async Task ToggleAvailability_IsPolicyGatedToAdministrators_AndAudited()
    {
        await SeedEmailProviderAsync();
        var policyEngine = Factory.Services.GetRequiredService<IPolicyEngine>();

        // The page consults the Policy Engine before mutating — administrators pass...
        using var client = CreateClient();
        var (_, adminId) = await LoginAsync(client, AdminUsername, AdminPassword);
        var adminDecision = await policyEngine.EvaluateAsync(new PolicyContext(
            adminId, PermissionActions.ProviderCatalogManage, "provider-catalog"));
        Assert.That(adminDecision.Allowed, Is.True);

        // ... and operators are denied.
        var (operatorId, _, _) = await CreatePrincipalAsync("Operator");
        var operatorDecision = await policyEngine.EvaluateAsync(new PolicyContext(
            operatorId, PermissionActions.ProviderCatalogManage, "provider-catalog"));
        Assert.That(operatorDecision.Allowed, Is.False);

        var entry = await Catalog.SetAvailabilityAsync(adminId.ToString("D"), "email-work-items", true);
        Assert.That(entry.Available, Is.True);

        var audit = await Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>().ReadAsync();
        Assert.That(audit.Any(a =>
                a.Action == "provider-catalog.availability-changed" &&
                a.Subject == "email-work-items" &&
                a.Actor == adminId.ToString("D") &&
                a.Outcome == "available"),
            Is.True, "the availability toggle must be audited");
    }

    [Test]
    public async Task ReadPath_ServesAvailableProviders_GroupedByCategory_WithMergedDescriptors()
    {
        await SeedEmailProviderAsync();

        await Catalog.SetCategoryAsync("test-admin", "email-work-items", "task-source");
        await Catalog.SetAvailabilityAsync("test-admin", "email-work-items", true);
        await Catalog.SetOverridesAsync("test-admin", "email-work-items",
            [new SettingDescriptorOverride("Folder", Label: "Ticket folder", DefaultValue: "Tickets")]);

        var byCategory = await Catalog.ListAvailableByCategoryAsync();

        Assert.That(byCategory.ContainsKey("task-source"), Is.True);
        var email = byCategory["task-source"].Single(e => e.ProviderType == "email-work-items");
        var folder = email.Descriptors.Single(d => d.Key == "Folder");
        Assert.Multiple(() =>
        {
            Assert.That(folder.Label, Is.EqualTo("Ticket folder"));
            Assert.That(folder.DefaultValue, Is.EqualTo("Tickets"));
            Assert.That(folder.Kind, Is.EqualTo(SettingKind.Text), "kinds stay manifest-owned");
            Assert.That(email.Descriptors.Single(d => d.Key == "Password").Kind,
                Is.EqualTo(SettingKind.Secret));
        });
    }

    [Test]
    public async Task CatalogPage_AsAdministrator_ListsProviderWithDescriptorKinds()
    {
        await SeedEmailProviderAsync();
        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var html = await GetHtmlAsync(client, "/admin/provider-catalog", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Registered providers"));
            Assert.That(html, Does.Contain("email-work-items"));
            Assert.That(html, Does.Contain("Password · Secret"));
            Assert.That(html, Does.Not.Contain("Access denied"));
        });
    }

    [Test]
    public async Task CatalogPage_AsOperator_IsDeniedByPolicy()
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, "/admin/provider-catalog", cookie);

        Assert.That(html, Does.Contain("Access denied"));
    }
}

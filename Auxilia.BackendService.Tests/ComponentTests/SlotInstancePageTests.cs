using System.Text.Json;
using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// The reusable slot instances surface through the real DI container: the operator page
/// renders instances without settings values, and the service publishes seed commands (the
/// Steering Instance stays the single writer).
/// </summary>
[TestFixture]
[Category("Component")]
public class SlotInstancePageTests : DashboardComponentTestBase
{
    private IDataAccess<SlotInstanceRecord> Instances
        => Factory.Services.GetRequiredService<IDataAccess<SlotInstanceRecord>>();

    private async Task SeedInstanceRecordAsync(string name, string displayName)
    {
        var protector = Factory.Services.GetRequiredService<ISettingsProtector>();
        await Instances.SaveAsync(new SlotInstanceRecord
        {
            Id = SlotInstanceRecord.IdFor(name),
            Name = name,
            DisplayName = displayName,
            ProviderType = "email-work-items",
            ProtectedSettingsJson = protector.Protect(JsonSerializer.Serialize(
                new Dictionary<string, string> { ["Password"] = "instance-secret" })),
            Scope = SlotInstanceScope.Company
        });
    }

    [Test]
    public async Task SlotsPage_RendersInstances_WithoutSettingsValues()
    {
        await SeedInstanceRecordAsync("page-render-demo", "Page render demo");

        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, "/operator/slots", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Slot instances"));
            Assert.That(html, Does.Contain("Page render demo"));
            Assert.That(html, Does.Contain("New slot instance"));
            Assert.That(html, Does.Not.Contain("instance-secret"),
                "settings values must never render");
        });

        await Instances.RemoveAsync(SlotInstanceRecord.IdFor("page-render-demo"));
    }

    [Test]
    public async Task SaveAndDelete_TravelOverTheSeedingExchange()
    {
        var providers = Factory.Services.GetRequiredService<IDataAccess<SlotProviderRecord>>();
        await providers.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor("email-work-items"),
            ProviderType = "email-work-items",
            DllPath = "/plugins/email.slothandler.dll"
        });

        var service = Factory.Services.GetRequiredService<SlotInstanceService>();
        var draft = new SlotInstanceDraft { DisplayName = "Bus demo", ProviderType = "email-work-items" };
        draft.Settings["ImapHost"] = "imap.example.org";

        var name = await service.SaveAsync("component-test", Guid.NewGuid(), draft);

        var upsert = MessageBus.PublishedMessages
            .Where(p => p.Topic == "slot-configurations")
            .Select(p => p.Message).OfType<UpsertSlotInstanceCommand>()
            .Single(c => c.Name == name);
        Assert.That(upsert.Settings["ImapHost"], Is.EqualTo("imap.example.org"));

        await SeedInstanceRecordAsync(name, "Bus demo");
        await service.DeleteAsync("component-test", SlotInstanceRecord.IdFor(name));

        var removal = MessageBus.PublishedMessages
            .Select(p => p.Message).OfType<RemoveSlotInstanceCommand>()
            .SingleOrDefault(c => c.Name == name);
        Assert.That(removal, Is.Not.Null, "the removal must travel over the bus — the SI owns the store");

        await Instances.RemoveAsync(SlotInstanceRecord.IdFor(name));
    }
}

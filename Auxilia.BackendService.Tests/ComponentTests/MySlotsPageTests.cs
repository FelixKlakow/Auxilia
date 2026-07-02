using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>The self-service "my slots" page: personal instances only, own vs assigned.</summary>
[TestFixture]
[Category("Component")]
public class MySlotsPageTests : DashboardComponentTestBase
{
    [Test]
    public async Task MySlotsPage_ListsOwnedAndAssignedPersonalInstances()
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("User");
        var (cookie, principalId) = await LoginAsync(client, username, password);

        var instances = Factory.Services.GetRequiredService<IDataAccess<SlotInstanceRecord>>();
        await instances.SaveAsync(new SlotInstanceRecord
        {
            Id = SlotInstanceRecord.IdFor($"own-seat-{principalId:N}"),
            Name = $"own-seat-{principalId:N}",
            DisplayName = "My license seat",
            ProviderType = "claude-code-cli",
            ProtectedSettingsJson = "{}",
            Scope = SlotInstanceScope.Personal,
            OwnerPrincipalId = principalId
        });
        await instances.SaveAsync(new SlotInstanceRecord
        {
            Id = SlotInstanceRecord.IdFor($"assigned-seat-{principalId:N}"),
            Name = $"assigned-seat-{principalId:N}",
            DisplayName = "Assigned company seat",
            ProviderType = "claude-code-cli",
            ProtectedSettingsJson = "{}",
            Scope = SlotInstanceScope.Personal,
            OwnerPrincipalId = Guid.NewGuid(),
            AssignedPrincipalIdsJson = JsonSerializer.Serialize(new[] { principalId })
        });

        var html = await GetHtmlAsync(client, "/my-slots", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("My license seat"));
            Assert.That(html, Does.Contain("Assigned company seat"));
            Assert.That(html, Does.Contain("assigned to you"), "assigned instances are marked read-only");
            Assert.That(html, Does.Contain("New personal instance"));
            Assert.That(html, Does.Not.Contain("Access denied"));
        });
    }
}

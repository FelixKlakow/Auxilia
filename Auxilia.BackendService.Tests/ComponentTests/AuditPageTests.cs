using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>The audit page resolves GUID actors to principal names and renders the toolbar.</summary>
[TestFixture]
[Category("Component")]
public class AuditPageTests : DashboardComponentTestBase
{
    [Test]
    public async Task AuditPage_ShowsPrincipalNames_InsteadOfActorGuids()
    {
        var (principalId, _, _) = await CreatePrincipalAsync("Operator");
        var audit = Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>();
        await audit.SaveAsync(new AuditRecord
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTimeOffset.UtcNow,
            Actor = principalId.ToString("D"),
            Action = "workflow.rerun",
            Subject = "some-run",
            Outcome = "dispatched"
        });

        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var html = await GetHtmlAsync(client, "/audit", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Operator test user"),
                "GUID actors resolve to the principal's display name");
            Assert.That(html, Does.Contain("workflow.rerun"));
            Assert.That(html, Does.Contain("type=\"date\""), "the date-range filter renders");
        });
    }
}

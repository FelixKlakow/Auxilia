using System.Text.Json;
using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Dashboard composition round-trip: pin a view, see it rendered (with persisted replay)
/// on the dashboard home page, unpin it again.
/// </summary>
[TestFixture]
[Category("Component")]
public class DashboardCompositionTests : DashboardComponentTestBase
{
    private readonly Guid _instanceId = Guid.NewGuid();

    private DashboardComposer Composer => Factory.Services.GetRequiredService<DashboardComposer>();

    [OneTimeSetUp]
    public async Task SeedRunWithViewData()
    {
        var viewsJson = JsonSerializer.Serialize(new List<ViewDescriptor>
        {
            new("review-log", "{}", ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
        });
        await Factory.Services.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>()
            .SaveAsync(new WorkflowInstanceRecord
            {
                Id = _instanceId,
                WorkflowType = "code-review",
                State = "Running",
                CreatedUtc = DateTimeOffset.UtcNow,
                ViewsJson = viewsJson
            });
        await Factory.Services.GetRequiredService<IDataAccess<ViewDataRecord>>()
            .SaveAsync(new ViewDataRecord
            {
                Id = ViewDataRecord.IdFor(_instanceId, "review-log", 1),
                WorkflowInstanceId = _instanceId,
                ViewName = "review-log",
                Sequence = 1,
                PayloadJson = """{"line":"hello-from-store"}""",
                TimestampUtc = DateTimeOffset.UtcNow
            });
    }

    [Test]
    public async Task PinUnpinRoundTrip_DashboardRendersPinnedViewFromPersistedReplay()
    {
        using var client = CreateClient();
        var (cookie, principalId) = await LoginAsync(client, AdminUsername, AdminPassword);

        // Pin → the dashboard home page renders the view through the generic ViewRenderer.
        await Composer.PinAsync(principalId, principalId,
            new DashboardPin(_instanceId, "review-log", "Pinned review log"));
        var html = await GetHtmlAsync(client, "/dashboard", cookie);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Pinned review log"));
            Assert.That(html, Does.Contain("hello-from-store"), "persisted view data must be replayed");
        });

        // Unpin → the panel disappears again.
        await Composer.UnpinAsync(principalId, principalId, _instanceId, "review-log");
        html = await GetHtmlAsync(client, "/dashboard", cookie);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("Pinned review log"));
            Assert.That(html, Does.Contain("Nothing pinned yet"));
        });
    }

    [Test]
    public async Task RunDetailPage_ShowsPinAffordance()
    {
        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var html = await GetHtmlAsync(client, $"/runs/{_instanceId}", cookie);

        Assert.That(html, Does.Contain("Pin to dashboard"));
    }

    [Test]
    public async Task PinAsync_AsUserWithoutViewAccessToWorkflowType_StillFollowsPolicy()
    {
        var (userId, _, _) = await CreatePrincipalAsync("Auditor"); // Auditor lacks view.subscribe

        Assert.That(
            () => Composer.PinAsync(userId, userId, new DashboardPin(_instanceId, "review-log", "X")),
            Throws.InvalidOperationException.With.Message.Contains("view.subscribe denied"));
    }
}

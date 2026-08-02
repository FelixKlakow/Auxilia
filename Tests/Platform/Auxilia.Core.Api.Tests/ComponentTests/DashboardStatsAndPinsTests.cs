using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The dashboard client surface: server-side aggregate run counts (GET /api/runs/stats) and
/// personal dashboard pins (GET/POST/DELETE /api/dashboard/pins, principal-scoped, idempotent
/// per run+view).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class DashboardStatsAndPinsTests : CoreApiComponentTestBase
{
    private Task SeedRunAsync(Guid instanceId, string type, string state)
        => MessageBus.SimulateReceivedAsync(
            WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, type, state, null, DateTimeOffset.UtcNow));

    [Test]
    public async Task Stats_AggregateCountsByState()
    {
        var client = CreateClient();
        await SeedRunAsync(Guid.NewGuid(), "impl", "Running");
        await SeedRunAsync(Guid.NewGuid(), "impl", "Success");
        await SeedRunAsync(Guid.NewGuid(), "review", "Failed");

        var stats = await client.GetFromJsonAsync<RunStats>("/api/runs/stats");

        Assert.Multiple(() =>
        {
            Assert.That(stats!.Total, Is.EqualTo(3));
            Assert.That(stats.Active, Is.EqualTo(1), "only the Running run is non-terminal");
            Assert.That(stats.ByState["Success"], Is.EqualTo(1));
            Assert.That(stats.ByState["Failed"], Is.EqualTo(1));
            Assert.That(stats.ByState["Running"], Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Pins_CreateListDelete_RoundTrip()
    {
        var client = CreateClient();
        var runId = Guid.NewGuid();
        await SeedRunAsync(runId, "impl", "Success");

        var created = await client.PostAsJsonAsync(
            "/api/dashboard/pins", new CreateDashboardPin(runId, "progress"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var pin = await created.Content.ReadFromJsonAsync<DashboardPin>();
        Assert.That(pin!.WorkflowType, Is.EqualTo("impl"), "the workflow type is denormalized at pin time");

        // Pinning the same view again stays ONE pin (deterministic id).
        var again = await (await client.PostAsJsonAsync(
            "/api/dashboard/pins", new CreateDashboardPin(runId, "progress"))).Content
            .ReadFromJsonAsync<DashboardPin>();
        Assert.That(again!.Id, Is.EqualTo(pin.Id));

        var pins = await client.GetFromJsonAsync<List<DashboardPin>>("/api/dashboard/pins");
        Assert.That(pins!.Select(p => p.Id), Is.EqualTo(new[] { pin.Id }));

        var delete = await client.DeleteAsync($"/api/dashboard/pins/{pin.Id}");
        Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var after = await client.GetFromJsonAsync<List<DashboardPin>>("/api/dashboard/pins");
        Assert.That(after, Is.Empty);
    }

    [Test]
    public async Task Pin_UnknownRun_Returns404()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/dashboard/pins", new CreateDashboardPin(Guid.NewGuid(), "progress"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Pins_ResolveDispatchAlias_ToInstanceId()
    {
        var client = CreateClient();
        var commandId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        // The runner records under its own instance id, claiming the dispatch command id.
        await MessageBus.SimulateReceivedAsync(
            WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, "impl", "Running", null, DateTimeOffset.UtcNow, CommandId: commandId));

        var created = await client.PostAsJsonAsync(
            "/api/dashboard/pins", new CreateDashboardPin(commandId, "progress"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var pin = await created.Content.ReadFromJsonAsync<DashboardPin>();
        Assert.That(pin!.RunId, Is.EqualTo(instanceId),
            "a pin created via the dispatch id sticks to the instance id the view store uses");
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Per-run authorization (ARCHITECTURE §16 "Run access"): a run is reachable only by the
/// principal it runs for, the principal that started it on that one's behalf, the subjects its
/// grants admit, and administrators (audited). Holding the same role is not enough — another
/// principal finds no trace of the run on any surface: status, list, stream, stored views,
/// inputs, terminal, cancel/rerun, artifacts, events, MCP.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunAccessTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";

    [SetUp]
    public async Task RegisterDummyTypeAsync()
    {
        await RegisterActiveTypeAsync(CreateClient(), DummyType);
        // Run access is about WHICH runs — not the catalog's restricted default posture.
        await OpenDefaultResourceAccessAsync();
    }

    // ------------------------------------------------------------------ the acceptance case

    [Test]
    public async Task ASecondPrincipalWithTheSameRole_FindsNoTraceOfSomeoneElsesRun()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var (bob, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var runId = await StartLiveRunWithTerminalAsync(alice);

        Assert.Multiple(async () =>
        {
            foreach (var (surface, status) in await ProbeEverySurfaceAsync(bob, runId))
                Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound), $"{surface} must not reveal someone else's run");
            Assert.That(await ListedRunIdsAsync(bob), Does.Not.Contain(runId), "the list returns only the caller's runs");
            Assert.That((await bob.GetFromJsonAsync<RunStats>("/api/runs/stats"))!.Total, Is.Zero,
                "the dashboard counts only the caller's runs");
        });

        // The run's own principal reaches all of it.
        Assert.Multiple(async () =>
        {
            Assert.That(await ListedRunIdsAsync(alice), Does.Contain(runId));
            Assert.That((await alice.GetAsync($"/api/runs/{runId}")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await OpenStreamAsync(alice, runId)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await alice.GetAsync($"/api/runs/{runId}/views")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await SendInputAsync(alice, runId)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That((await alice.PostAsync($"/api/runs/{runId}/terminal-ticket", null)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public async Task SomeoneElsesRun_CannotBeCancelledRerunSharedOrPinned()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var (bob, bobId) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var runId = await StartLiveRunWithTerminalAsync(alice);

        Assert.Multiple(async () =>
        {
            Assert.That((await bob.PostAsync($"/api/runs/{runId}/cancel", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await bob.PostAsync($"/api/runs/{runId}/rerun", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await bob.PutAsJsonAsync($"/api/runs/{runId}/grants",
                new SetRunGrants([new AccessGrant(AccessGrantKind.Principal, bobId.ToString())]))).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound), "nobody grants themselves access");
            Assert.That((await bob.PostAsJsonAsync("/api/dashboard/pins", new CreateDashboardPin(runId, "log"))).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });
        Assert.That(MessageBus.PublishedMessages.Select(m => m.Message).OfType<CancelWorkflowCommand>(), Is.Empty);
    }

    // ------------------------------------------------------------------ sharing

    [Test]
    public async Task ARunSharedWithAPrincipal_IsReachableByThem_UntilTheGrantIsWithdrawn()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var (bob, bobId) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var runId = await StartLiveRunWithTerminalAsync(alice);

        var shared = await alice.PutAsJsonAsync($"/api/runs/{runId}/grants",
            new SetRunGrants([new AccessGrant(AccessGrantKind.Principal, bobId.ToString())]));
        Assert.That(shared.StatusCode, Is.EqualTo(HttpStatusCode.OK), await shared.Content.ReadAsStringAsync());
        Assert.That((await shared.Content.ReadFromJsonAsync<RunStatus>())!.Grants, Has.Count.EqualTo(1));

        Assert.Multiple(async () =>
        {
            Assert.That(await ListedRunIdsAsync(bob), Does.Contain(runId));
            Assert.That((await OpenStreamAsync(bob, runId)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await SendInputAsync(bob, runId)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
                "a grant admits steering too — the role still decides what the grantee may do");
            Assert.That((await bob.PutAsJsonAsync($"/api/runs/{runId}/grants", new SetRunGrants([]))).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden), "a grantee may not re-share or unshare the run");
        });

        var withdrawn = await alice.PutAsJsonAsync($"/api/runs/{runId}/grants", new SetRunGrants([]));
        Assert.That(withdrawn.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await bob.GetAsync($"/api/runs/{runId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "an empty grant list makes the run private again");
    }

    [Test]
    public async Task ARunSharedWithAGroup_IsReachableByItsMembersOnly()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var (bob, bobId) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var (carol, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var groups = Factory.Services.GetRequiredService<GroupDirectory>();
        var group = await groups.CreateAsync($"reviewers-{Guid.NewGuid():N}", ct: CancellationToken.None);
        await groups.AddMemberAsync(group.Id, bobId, CancellationToken.None);
        var runId = await StartLiveRunWithTerminalAsync(alice);

        await alice.PutAsJsonAsync($"/api/runs/{runId}/grants",
            new SetRunGrants([new AccessGrant(AccessGrantKind.Group, group.Id.ToString())]));

        Assert.That((await bob.GetAsync($"/api/runs/{runId}")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await carol.GetAsync($"/api/runs/{runId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task MalformedGrants_AreRejected()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var runId = await StartLiveRunWithTerminalAsync(alice);

        var response = await alice.PutAsJsonAsync($"/api/runs/{runId}/grants",
            new SetRunGrants([new AccessGrant("Everyone", "*")]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ------------------------------------------------------------------ delegation

    [Test]
    public async Task ADelegatedRun_IsReachableByWhoItRunsFor_AndItsStarter_AndNobodyElse()
    {
        // A client application (an Operator holding run.on-behalf-of) starts a run for a person.
        var (clientApp, clientAppId) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var (person, personId) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var (colleague, _) = await PrincipalWithRolesAsync(BuiltInRoles.User);

        var response = await clientApp.PostAsJsonAsync("/api/runs", new RunRequest(DummyType, RequestedBy: personId));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var runId = (await response.Content.ReadFromJsonAsync<RunAccepted>())!.RunId;

        var asPerson = await person.GetFromJsonAsync<RunStatus>($"/api/runs/{runId}");
        Assert.Multiple(async () =>
        {
            Assert.That(asPerson!.RunsFor, Is.EqualTo(personId));
            Assert.That(asPerson.StartedBy, Is.EqualTo(clientAppId));
            Assert.That((await clientApp.GetAsync($"/api/runs/{runId}")).StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "the starter keeps the run it started for someone");
            Assert.That((await colleague.GetAsync($"/api/runs/{runId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task OwnershipSurvivesTheRunnersClaim_WhichRekeysTheRunToItsInstanceId()
    {
        var (alice, aliceId) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var (bob, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var accepted = await StartRunAsync(alice);
        var instanceId = Guid.NewGuid();

        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, DummyType, "Received", null, DateTimeOffset.UtcNow,
                OwnerServiceId: Guid.NewGuid(), CommandId: accepted.RunId));

        var byInstance = await alice.GetFromJsonAsync<RunStatus>($"/api/runs/{instanceId}");
        Assert.Multiple(async () =>
        {
            Assert.That(byInstance!.RunsFor, Is.EqualTo(aliceId));
            Assert.That((await bob.GetAsync($"/api/runs/{instanceId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await bob.GetAsync($"/api/runs/{accepted.RunId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
                "the dispatch-id alias is guarded exactly like the instance id");
        });
    }

    // ------------------------------------------------------------------ administrators

    [Test]
    public async Task AnAdministrator_ReachesEveryRun_AndReachingSomeoneElsesIsAudited()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var runId = await StartLiveRunWithTerminalAsync(alice);
        var admin = CreateClient();

        Assert.That(await ListedRunIdsAsync(admin), Does.Contain(runId));
        using var stream = await OpenStreamAsync(admin, runId);
        Assert.That(stream.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var audit = await Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>().ReadAsync(CancellationToken.None);
        Assert.That(audit.AsEnumerable().Any(r => r.Action == "run.admin-access" && r.Subject == runId.ToString() && r.Outcome == "stream"),
            Is.True, "an administrator reading someone else's run leaves a record");
    }

    // ------------------------------------------------------------------ run output

    [Test]
    public async Task ArtifactsAndEventsOfSomeoneElsesRun_StayInvisible()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var (bob, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var runId = await StartLiveRunWithTerminalAsync(alice);
        var artifactId = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName, new ArtifactPersistedEvent(
            artifactId, "container-log", DummyType, "WI-1", runId, 1, "HASH", 10, DateTimeOffset.UtcNow));
        var eventId = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName, new WorkflowEventMessage(
            eventId, "review-ready", runId, DummyType, "WI-1", "{}", DateTimeOffset.UtcNow));

        Assert.Multiple(async () =>
        {
            Assert.That((await bob.GetFromJsonAsync<PagedResult<ArtifactDto>>("/api/artifacts"))!.Items.Select(a => a.Id),
                Does.Not.Contain(artifactId));
            Assert.That((await bob.GetAsync($"/api/artifacts/{artifactId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await bob.GetAsync($"/api/artifacts/{artifactId}/content")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await bob.GetFromJsonAsync<PagedResult<EventDto>>("/api/events"))!.Items.Select(e => e.Id),
                Does.Not.Contain(eventId));
            Assert.That((await bob.GetAsync($"/api/events/{eventId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            Assert.That((await alice.GetFromJsonAsync<PagedResult<ArtifactDto>>("/api/artifacts"))!.Items.Select(a => a.Id),
                Does.Contain(artifactId));
            Assert.That((await alice.GetFromJsonAsync<PagedResult<EventDto>>("/api/events"))!.Items.Select(e => e.Id),
                Does.Contain(eventId));
        });
    }

    // ------------------------------------------------------------------ MCP parity

    [Test]
    public async Task McpTools_ApplyTheSameRunAccess()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var (bob, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var runId = await StartLiveRunWithTerminalAsync(alice);

        var bobsList = await CallMcpToolAsync(bob, "list_runs", new { });
        var bobsGet = await CallMcpToolAsync(bob, "get_run", new { runId = runId.ToString() });
        var bobsCancel = await CallMcpToolAsync(bob, "cancel_run", new { runId = runId.ToString() });
        var alicesGet = await CallMcpToolAsync(alice, "get_run", new { runId = runId.ToString() });

        Assert.Multiple(() =>
        {
            Assert.That(bobsList, Does.Not.Contain(runId.ToString()));
            Assert.That(bobsGet, Does.Contain("run not found"));
            Assert.That(bobsCancel, Does.Contain("run not found"));
            Assert.That(alicesGet, Does.Contain(runId.ToString()));
        });
        Assert.That(MessageBus.PublishedMessages.Select(m => m.Message).OfType<CancelWorkflowCommand>(), Is.Empty);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(HttpClient Client, Guid PrincipalId)> PrincipalWithRolesAsync(params string[] roles)
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}");
        foreach (var role in roles)
            await directory.AssignRoleAsync(principal.Id, role);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return (client, principal.Id);
    }

    private static async Task<RunAccepted> StartRunAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/runs", new RunRequest(DummyType));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RunAccepted>())!;
    }

    /// <summary>A dispatched run the runner reports Running with a terminal — every surface has something to serve.</summary>
    private async Task<Guid> StartLiveRunWithTerminalAsync(HttpClient owner)
    {
        var accepted = await StartRunAsync(owner);
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(accepted.RunId, DummyType, "Running", null, DateTimeOffset.UtcNow,
                TerminalEndpoint: "127.0.0.1:7681"));
        return accepted.RunId;
    }

    private static async Task<HttpResponseMessage> OpenStreamAsync(HttpClient client, Guid runId)
        => await client.GetAsync($"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);

    private static Task<HttpResponseMessage> SendInputAsync(HttpClient client, Guid runId)
        => client.PostAsJsonAsync($"/api/runs/{runId}/inputs", new ProvideRunInput("""{"answer":"go"}"""));

    private static async Task<List<(string Surface, HttpStatusCode Status)>> ProbeEverySurfaceAsync(HttpClient client, Guid runId)
    {
        using var stream = await OpenStreamAsync(client, runId);
        return
        [
            ("status", (await client.GetAsync($"/api/runs/{runId}")).StatusCode),
            ("stream", stream.StatusCode),
            ("views", (await client.GetAsync($"/api/runs/{runId}/views")).StatusCode),
            ("inputs", (await SendInputAsync(client, runId)).StatusCode),
            ("terminal", (await client.PostAsync($"/api/runs/{runId}/terminal-ticket", null)).StatusCode),
        ];
    }

    private static async Task<IReadOnlyList<Guid>> ListedRunIdsAsync(HttpClient client)
        => (await client.GetFromJsonAsync<PagedResult<RunStatus>>("/api/runs?take=500"))!.Items.Select(r => r.RunId).ToList();

    /// <summary>One stateless MCP tools/call; returns the tool result's text content.</summary>
    private static async Task<string> CallMcpToolAsync(HttpClient client, string tool, object arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments }
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.IsSuccessStatusCode, Is.True, body);
        var json = body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("data:"))?["data:".Length..] ?? body;
        var result = JsonDocument.Parse(json).RootElement.GetProperty("result");
        return string.Concat(result.GetProperty("content").EnumerateArray()
            .Select(c => c.TryGetProperty("text", out var text) ? text.GetString() : ""));
    }
}

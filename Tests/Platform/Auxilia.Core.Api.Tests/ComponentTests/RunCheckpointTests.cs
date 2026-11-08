using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Run checkpoints through the Core's surface: the runner hands a save over with the run's
/// resolution token, the Core keeps it encrypted as the newest version of the run's checkpoint
/// (the previous one goes), it outlives the run, the run's principals download it — and delete
/// it, grantees only read — and its lifetime follows the most specific level the platform allows.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunCheckpointTests : CoreApiComponentTestBase
{
    private const string WorkflowType = "checkpoint-workflow";
    private const string Session = "session";

    private readonly string _payloadRoot = Path.Combine(Path.GetTempPath(), $"auxilia-checkpoints-{Guid.NewGuid():N}");

    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.UseSetting("CoreApi:MaxCheckpointBytes", "4096")
            .UseSetting("CoreApi:CheckpointLifetimeHours", "168")
            // Its own storage — never the machine's real platform-data folder.
            .UseSetting("ArtifactStore:PayloadRoot", _payloadRoot);

    [TearDown]
    public void RemovePayloads()
    {
        if (Directory.Exists(_payloadRoot))
            Directory.Delete(_payloadRoot, recursive: true);
    }

    [SetUp]
    public async Task DeclareTheWorkflow()
    {
        await RegisterActiveTypeAsync(CreateClient(), WorkflowType);
        // Checkpoints are about the run's own principals — not the catalog's restricted default.
        await OpenDefaultResourceAccessAsync();
        await MessageBus.SimulateReceivedAsync(WorkflowSchemaPublished.ExchangeName,
            new WorkflowSchemaPublished(WorkflowType,
                new WorkflowSchema(WorkflowType, [], [])
                {
                    Checkpoints = [new WorkflowCheckpointDescriptor(Session, "session", "the session to hand back", LifetimeHours: 48)],
                },
                DateTimeOffset.UtcNow));
    }

    [Test]
    public async Task ASavedCheckpoint_IsEncryptedAtRest_AndHandedBackToTheRunsPrincipal()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var run = await StartClaimedRunAsync(alice);
        var content = Encoding.UTF8.GetBytes("everything the agent read and did — alice's alone");

        var saved = await SaveOkAsync(run, content);

        var listed = await alice.GetFromJsonAsync<PagedResult<ArtifactDto>>($"/api/artifacts?runId={run.CommandId}");
        using var download = await alice.GetAsync($"/api/artifacts/{saved.Id}/content");
        var storage = Factory.Services.GetRequiredService<RunCheckpointStorage>();
        Assert.Multiple(async () =>
        {
            Assert.That(saved.Kind, Is.EqualTo(ArtifactKinds.Checkpoint));
            Assert.That(saved.Version, Is.EqualTo(1));
            Assert.That(saved.RunInstanceId, Is.EqualTo(run.InstanceId), "the run's instance, after the claim's rekey");
            Assert.That(saved.MediaType, Is.EqualTo("application/gzip"));
            Assert.That(saved.ExpiresUtc, Is.EqualTo(DateTimeOffset.UtcNow.AddHours(48)).Within(TimeSpan.FromMinutes(1)),
                "the workflow's declared lifetime");
            Assert.That(listed!.Items.Select(a => a.Id), Does.Contain(saved.Id), "an artifact of the run");
            Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await download.Content.ReadAsByteArrayAsync(), Is.EqualTo(content));
            Assert.That(download.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/gzip"));
            Assert.That(download.Content.Headers.ContentDisposition?.FileName, Is.EqualTo("session-v1.tar.gz"));
            Assert.That(Directory.GetFiles(storage.Directory).Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))),
                Has.None.Contains("agent read"), "never on disk in the clear");
        });
    }

    [Test]
    public async Task ASecondSave_ReplacesTheFirst_AndTheLastOneOutlivesTheRun()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var run = await StartClaimedRunAsync(alice);

        var first = await SaveOkAsync(run, "turn 1"u8.ToArray());
        var second = await SaveOkAsync(run, "turn 2"u8.ToArray());
        await EndRunAsync(run, "Failed");

        using var late = await SaveAsync(run, "after the end"u8.ToArray());
        using var download = await alice.GetAsync($"/api/artifacts/{second.Id}/content");
        Assert.Multiple(async () =>
        {
            Assert.That(second.Version, Is.EqualTo(2));
            Assert.That((await alice.GetAsync($"/api/artifacts/{first.Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
                "replaced once the newer copy was stored");
            Assert.That(await download.Content.ReadAsStringAsync(), Is.EqualTo("turn 2"), "a failed run keeps its last save");
            Assert.That(late.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "the token is a capability of a live run");
        });
    }

    [Test]
    public async Task OnlyTheRunsOwnPrincipals_DeleteIt_AndOnlyCheckpointsAreDeletable()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var (bob, bobId) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var (carol, _) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var run = await StartClaimedRunAsync(alice);
        var saved = await SaveOkAsync(run, "state"u8.ToArray());
        var shared = await alice.PutAsJsonAsync($"/api/runs/{run.CommandId}/grants",
            new SetRunGrants([new AccessGrant(AccessGrantKind.Principal, bobId.ToString())]));
        Assert.That(shared.StatusCode, Is.EqualTo(HttpStatusCode.OK), await shared.Content.ReadAsStringAsync());
        var output = new CoreArtifactRecord
        {
            Id = Guid.NewGuid(), ArtifactType = "report", WorkflowType = WorkflowType, RunInstanceId = run.InstanceId,
            Version = 1, ContentHash = "abc", SizeBytes = 3, CreatedUtc = DateTimeOffset.UtcNow,
        };
        await Factory.Services.GetRequiredService<IDataAccess<CoreArtifactRecord>>().SaveAsync(output);

        using var byGrantee = await bob.DeleteAsync($"/api/artifacts/{saved.Id}");
        using var byStranger = await carol.DeleteAsync($"/api/artifacts/{saved.Id}");
        using var anOutput = await alice.DeleteAsync($"/api/artifacts/{output.Id}");
        using var byOwner = await alice.DeleteAsync($"/api/artifacts/{saved.Id}");

        Assert.Multiple(async () =>
        {
            Assert.That((await bob.GetAsync($"/api/artifacts/{saved.Id}/content")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
                "gone after the owner's delete");
            Assert.That(byGrantee.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "a grantee reads, never deletes");
            Assert.That(byStranger.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "someone else's run leaves no trace");
            Assert.That(anOutput.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "the platform keeps every other artifact");
            Assert.That(byOwner.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(Directory.GetFiles(Factory.Services.GetRequiredService<RunCheckpointStorage>().Directory),
                Is.Empty, "the payload went with it");
        });
    }

    [Test]
    public async Task ClearingTheHistory_KeepsARunThatStillHoldsCheckpoints_UntilTheyAreGone()
    {
        var client = CreateClient();
        var holding = await StartClaimedRunAsync(client);
        var saved = await SaveOkAsync(holding, "state"u8.ToArray());
        await EndRunAsync(holding, "Success");
        var other = await StartClaimedRunAsync(client);
        await EndRunAsync(other, "Success");

        var cleared = await (await client.DeleteAsync("/api/runs")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        using var stillThere = await client.GetAsync($"/api/runs/{holding.InstanceId}");
        using var download = await client.GetAsync($"/api/artifacts/{saved.Id}/content");
        using var deleted = await client.DeleteAsync($"/api/artifacts/{saved.Id}");
        var clearedAfter = await (await client.DeleteAsync("/api/runs")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Multiple(() =>
        {
            Assert.That(cleared.GetProperty("deleted").GetInt32(), Is.EqualTo(1));
            Assert.That(cleared.GetProperty("retainedForCheckpoints").GetInt32(), Is.EqualTo(1));
            Assert.That(stillThere.StatusCode, Is.EqualTo(HttpStatusCode.OK), "its principals reach the checkpoint through it");
            Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(clearedAfter.GetProperty("deleted").GetInt32(), Is.EqualTo(1), "cleared once nothing holds it");
        });
    }

    [Test]
    public async Task OverMcp_TheSameRulesDelete()
    {
        var (alice, _) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var (bob, bobId) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var run = await StartClaimedRunAsync(alice);
        var saved = await SaveOkAsync(run, "state"u8.ToArray());
        await alice.PutAsJsonAsync($"/api/runs/{run.CommandId}/grants",
            new SetRunGrants([new AccessGrant(AccessGrantKind.Principal, bobId.ToString())]));

        var byGrantee = await CallMcpToolAsync(bob, "delete_artifact", new { artifactId = saved.Id.ToString() });
        var byOwner = await CallMcpToolAsync(alice, "delete_artifact", new { artifactId = saved.Id.ToString() });

        Assert.Multiple(async () =>
        {
            Assert.That(byGrantee, Does.Contain("only the run's own principals"));
            Assert.That(byOwner, Does.Contain(saved.Id.ToString()));
            Assert.That((await alice.GetAsync($"/api/artifacts/{saved.Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task ARunsLifetime_OverridesTheWorkflows_WhereThePlatformLetsIt()
    {
        var client = CreateClient();
        var run = await StartClaimedRunAsync(client, new RunRequest(WorkflowType) { CheckpointLifetimeHours = 6 });

        var saved = await SaveOkAsync(run, "state"u8.ToArray());

        Assert.That(saved.ExpiresUtc, Is.EqualTo(DateTimeOffset.UtcNow.AddHours(6)).Within(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task APlatformThatDoesNotLetRunsSetIt_Or_ACap_RefusesTheRequest_AndCutsTheWorkflows()
    {
        var client = CreateClient();
        var settings = Factory.Services.GetRequiredService<PlatformSettingsService>();

        await settings.SetAsync(null, PlatformSettingKeys.CheckpointLifetimeOverrides, CheckpointLifetimeOverrideModes.Workflow, default);
        using var notLetToSetIt = await client.PostAsJsonAsync("/api/runs",
            new RunRequest(WorkflowType) { CheckpointLifetimeHours = 6 });

        await settings.SetAsync(null, PlatformSettingKeys.CheckpointLifetimeOverrides, CheckpointLifetimeOverrideModes.Run, default);
        await settings.SetAsync(null, PlatformSettingKeys.CheckpointLifetimeMaxHours, "24", default);
        using var aboveTheCap = await client.PostAsJsonAsync("/api/runs",
            new RunRequest(WorkflowType) { CheckpointLifetimeHours = 25 });
        var capped = await SaveOkAsync(await StartClaimedRunAsync(client), "state"u8.ToArray());

        Assert.Multiple(async () =>
        {
            Assert.That(notLetToSetIt.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), await notLetToSetIt.Content.ReadAsStringAsync());
            Assert.That(aboveTheCap.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(capped.ExpiresUtc, Is.EqualTo(DateTimeOffset.UtcNow.AddHours(24)).Within(TimeSpan.FromMinutes(1)),
                "the workflow's 48 hours, cut to the cap");
        });
    }

    [Test]
    public async Task AnUndeclaredName_ATokenOfAnotherRun_AForeignInstance_OrTooMuch_IsRefused()
    {
        var client = CreateClient();
        var run = await StartClaimedRunAsync(client);
        var other = await StartClaimedRunAsync(client);
        // Not claimed yet: the Core cannot tell which instance runs it.
        var unclaimed = await StartClaimedRunAsync(client, claim: false);

        using var undeclared = await SaveAsync(run, "x"u8.ToArray(), name: "secrets");
        using var wrongToken = await SaveAsync(run with { Token = other.Token }, "x"u8.ToArray());
        using var foreignInstance = await SaveAsync(run with { InstanceId = other.InstanceId }, "x"u8.ToArray());
        using var unclaimedNamingAnother = await SaveAsync(unclaimed with { InstanceId = other.InstanceId }, "x"u8.ToArray());
        using var unclaimedNamingItsOwn = await SaveAsync(unclaimed, "x"u8.ToArray());
        using var tooLarge = await SaveAsync(run, new byte[4097]);

        Assert.Multiple(() =>
        {
            Assert.That(undeclared.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(wrongToken.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(foreignInstance.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "never onto another run");
            Assert.That(unclaimedNamingAnother.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
                "nor onto another run before the claim");
            Assert.That(unclaimedNamingItsOwn.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
                "a save waits for the claim to be recorded");
            Assert.That(tooLarge.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        });
    }

    // ------------------------------------------------------------------ helpers

    private sealed record LiveRun(Guid CommandId, Guid InstanceId, string Token);

    private async Task<LiveRun> StartClaimedRunAsync(HttpClient owner, RunRequest? request = null, bool claim = true)
    {
        using var response = await owner.PostAsJsonAsync("/api/runs", request ?? new RunRequest(WorkflowType));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var accepted = (await response.Content.ReadFromJsonAsync<RunAccepted>())!;
        var command = MessageBus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>()
            .Single(c => c.CommandId == accepted.CommandId);
        // The runner's claim: the run continues under the instance id it assigned.
        var instanceId = Guid.NewGuid();
        if (claim)
            await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
                new WorkflowStatusEvent(instanceId, WorkflowType, "Running", null, DateTimeOffset.UtcNow,
                    CommandId: accepted.CommandId));
        return new LiveRun(accepted.CommandId, instanceId, command.ResolutionToken!);
    }

    private Task EndRunAsync(LiveRun run, string state)
        => MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(run.InstanceId, WorkflowType, state, null, DateTimeOffset.UtcNow,
                CommandId: run.CommandId));

    private Task<HttpResponseMessage> SaveAsync(LiveRun run, byte[] content, string name = Session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/runs/{run.CommandId}/checkpoints/{name}")
        {
            Content = new ByteArrayContent(content) { Headers = { ContentType = new MediaTypeHeaderValue("application/gzip") } },
        };
        request.Headers.Add("X-Resolution-Token", run.Token);
        request.Headers.Add("X-Workflow-Instance", run.InstanceId.ToString());
        return Factory.CreateClient().SendAsync(request);
    }

    private async Task<ArtifactDto> SaveOkAsync(LiveRun run, byte[] content)
    {
        using var response = await SaveAsync(run, content);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ArtifactDto>())!;
    }

    private static async Task<string> CallMcpToolAsync(HttpClient client, string tool, object arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
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
        var result = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("result");
        return string.Concat(result.GetProperty("content").EnumerateArray()
            .Select(c => c.TryGetProperty("text", out var text) ? text.GetString() : ""));
    }

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
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Run-on-behalf-of at the REST surface: a service/automation caller holding <c>run.on-behalf-of</c>
/// may dispatch a run AS a target principal, the target is still gated by <c>workflow.trigger</c>,
/// the delegation grants no capability the target lacks, and the run is stamped with the target as
/// its triggering principal. Absent a distinct target, behaviour is exactly a direct manual run.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunOnBehalfOfTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";
    private const string DummyImage = "docker://auxilia-dummy-workflows:system-test";

    [SetUp]
    public Task RegisterDummyTypeAsync() => RegisterActiveTypeAsync(CreateClient(), DummyType, DummyImage);


    private async Task<(HttpClient Client, Guid PrincipalId)> PrincipalWithRolesAsync(params string[] roles)
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}", "Service");
        foreach (var role in roles.Where(BuiltInRoles.Exists))
            await directory.AssignRoleAsync(principal.Id, role);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return (client, principal.Id);
    }

    private async Task<Guid> NewPrincipalAsync(params string[] roles)
        => (await PrincipalWithRolesAsync(roles)).PrincipalId;

    private async Task<Guid?> TriggeredByOfAsync(Guid runId)
    {
        var store = Factory.Services.GetRequiredService<IDataAccess<CoreRunResolutionRecord>>();
        var record = await store.ReadAsync(runId, CancellationToken.None);
        return record?.TriggeredByPrincipalId;
    }

    [Test]
    public async Task CallerWithOnBehalfOf_RunsAsTarget_StampsTargetAsTriggeringPrincipal()
    {
        // Operator holds run.on-behalf-of; the target is a plain User (allowed to trigger).
        var (caller, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var target = await NewPrincipalAsync(BuiltInRoles.User);

        var response = await caller.PostAsJsonAsync("/api/runs",
            new RunRequest(DummyType, RequestedBy: target));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var accepted = await response.Content.ReadFromJsonAsync<RunAccepted>();
        Assert.That(accepted, Is.Not.Null);
        Assert.That(await TriggeredByOfAsync(accepted!.RunId), Is.EqualTo(target),
            "The run must be attributed to the target principal, not the caller.");
    }

    [Test]
    public async Task CallerWithoutOnBehalfOf_NamingADifferentTarget_IsForbidden()
    {
        // A plain User can trigger their own runs but cannot act on behalf of anyone else.
        var (caller, _) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var target = await NewPrincipalAsync(BuiltInRoles.User);

        var response = await caller.PostAsJsonAsync("/api/runs",
            new RunRequest(DummyType, RequestedBy: target));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task TargetThatCannotTrigger_IsForbidden_EvenWhenCallerCanDelegate()
    {
        // Caller holds the delegation permission, but the target (Auditor) fails workflow.trigger.
        var (caller, _) = await PrincipalWithRolesAsync(BuiltInRoles.Administrator);
        var target = await NewPrincipalAsync(BuiltInRoles.Auditor);

        var response = await caller.PostAsJsonAsync("/api/runs",
            new RunRequest(DummyType, RequestedBy: target));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "Delegation grants no capability the target itself lacks.");
    }

    [Test]
    public async Task RequestedByEqualsCaller_BehavesAsADirectRun()
    {
        var (caller, callerId) = await PrincipalWithRolesAsync(BuiltInRoles.User);

        var response = await caller.PostAsJsonAsync("/api/runs",
            new RunRequest(DummyType, RequestedBy: callerId));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var accepted = await response.Content.ReadFromJsonAsync<RunAccepted>();
        Assert.That(await TriggeredByOfAsync(accepted!.RunId), Is.EqualTo(callerId));
    }

    [Test]
    public async Task RequestedByNull_StampsTheCallerAsTriggeringPrincipal()
    {
        var (caller, callerId) = await PrincipalWithRolesAsync(BuiltInRoles.User);

        var response = await caller.PostAsJsonAsync("/api/runs",
            new RunRequest(DummyType));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var accepted = await response.Content.ReadFromJsonAsync<RunAccepted>();
        Assert.That(await TriggeredByOfAsync(accepted!.RunId), Is.EqualTo(callerId));
    }
}

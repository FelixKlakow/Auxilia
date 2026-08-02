using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Run-on-behalf-of for the stored-configuration dispatch endpoint (the path WorkflowStudio's triggers
/// use): a caller holding <c>run.on-behalf-of</c> may dispatch a stored configuration AS a target
/// principal, the target is still gated by <c>workflow.trigger</c>, and the run is stamped with the
/// target as its triggering principal. Absent a distinct target, behaviour is a direct manual run.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunConfigurationOnBehalfOfTests : CoreApiComponentTestBase
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

    private async Task<Guid> SeedConfigurationAsync()
    {
        var configurations = Factory.Services.GetRequiredService<RunConfigurationService>();
        var config = await configurations.CreateAsync(new CreateRunConfiguration(
            "cfg-" + Guid.NewGuid().ToString("N"), DummyType,
            new Dictionary<string, string> { ["WORKFLOW_NAME"] = DummyType },
            Scope: ResourceScope.Company), ownerPrincipalId: null, CancellationToken.None);
        return config.Id;
    }

    private async Task<Guid?> TriggeredByOfAsync(Guid runId)
    {
        var store = Factory.Services.GetRequiredService<IDataAccess<CoreRunResolutionRecord>>();
        var record = await store.ReadAsync(runId, CancellationToken.None);
        return record?.TriggeredByPrincipalId;
    }

    [Test]
    public async Task CallerWithOnBehalfOf_RunsConfigAsTarget_StampsTargetAsTriggeringPrincipal()
    {
        var (caller, _) = await PrincipalWithRolesAsync(BuiltInRoles.Operator);
        var target = await NewPrincipalAsync(BuiltInRoles.User);
        var configId = await SeedConfigurationAsync();

        var response = await caller.PostAsync($"/api/configurations/{configId}/run?onBehalfOf={target}", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var accepted = await response.Content.ReadFromJsonAsync<RunAccepted>();
        Assert.That(accepted, Is.Not.Null);
        Assert.That(await TriggeredByOfAsync(accepted!.RunId), Is.EqualTo(target),
            "The run must be attributed to the target principal, not the caller.");
    }

    [Test]
    public async Task CallerWithoutOnBehalfOf_NamingADifferentTarget_IsForbidden()
    {
        var (caller, _) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var target = await NewPrincipalAsync(BuiltInRoles.User);
        var configId = await SeedConfigurationAsync();

        var response = await caller.PostAsync($"/api/configurations/{configId}/run?onBehalfOf={target}", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task TargetThatCannotTrigger_IsForbidden_EvenWhenCallerCanDelegate()
    {
        var (caller, _) = await PrincipalWithRolesAsync(BuiltInRoles.Administrator);
        var target = await NewPrincipalAsync(BuiltInRoles.Auditor);
        var configId = await SeedConfigurationAsync();

        var response = await caller.PostAsync($"/api/configurations/{configId}/run?onBehalfOf={target}", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "Delegation grants no capability the target itself lacks.");
    }

    [Test]
    public async Task OnBehalfOfEqualsCaller_BehavesAsADirectRun()
    {
        var (caller, callerId) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var configId = await SeedConfigurationAsync();

        var response = await caller.PostAsync($"/api/configurations/{configId}/run?onBehalfOf={callerId}", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var accepted = await response.Content.ReadFromJsonAsync<RunAccepted>();
        Assert.That(await TriggeredByOfAsync(accepted!.RunId), Is.EqualTo(callerId));
    }

    [Test]
    public async Task OnBehalfOfNull_StampsTheCallerAsTriggeringPrincipal()
    {
        var (caller, callerId) = await PrincipalWithRolesAsync(BuiltInRoles.User);
        var configId = await SeedConfigurationAsync();

        var response = await caller.PostAsync($"/api/configurations/{configId}/run", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var accepted = await response.Content.ReadFromJsonAsync<RunAccepted>();
        Assert.That(await TriggeredByOfAsync(accepted!.RunId), Is.EqualTo(callerId));
    }
}

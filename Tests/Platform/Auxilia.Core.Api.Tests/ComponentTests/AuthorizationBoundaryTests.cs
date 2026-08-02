using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Per-role authorization enforcement at the REST surface: administration is Administrator-only,
/// configuration is Operator+, triggering is User+, and everything sensitive is deny-by-default.
/// Also covers a role gained through group membership, and that connector reads never leak secrets.
/// Each client is a real API-key principal carrying exactly the role(s) under test.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AuthorizationBoundaryTests : CoreApiComponentTestBase
{
    [SetUp]
    public Task RegisterRunTypeAsync() => RegisterActiveTypeAsync(CreateClient(), "wt", "docker://img");

    private async Task<HttpClient> ClientForRolesAsync(params string[] roles)
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}", "Service");
        foreach (var role in roles.Where(BuiltInRoles.Exists))
            await directory.AssignRoleAsync(principal.Id, role);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static object NewConnector(string scope) =>
        new { name = $"c-{Guid.NewGuid():N}", providerType = "github", settings = new { token = "x" }, scope };

    // --- Administration: principal.administer / identity-source.manage → Administrator only ---

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.Auditor, HttpStatusCode.Forbidden)]
    public async Task CreateGroup_IsAdministratorOnly(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.PostAsJsonAsync("/api/groups", new { name = $"g-{Guid.NewGuid():N}" });
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    public async Task CreateGroupMapping_IsAdministratorOnly(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.PostAsJsonAsync("/api/identity/group-mappings",
            new { identityProvider = "entra", groupClaim = $"grp-{Guid.NewGuid():N}", roleName = "User" });
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.Auditor, HttpStatusCode.Forbidden)]
    public async Task SaveIdentitySource_IsAdministratorOnly(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.PostAsJsonAsync("/api/identity/sources", new
        {
            name = $"src-{Guid.NewGuid():N}",
            connectorType = "csv",
            settings = new Dictionary<string, string> { ["Csv"] = "ada,ada" },
            defaultRole = "User",
            groupRoleMappings = new Dictionary<string, string>(),
            disableMissing = false
        });
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    // --- Configuration: slot-config.write → Operator and above ---

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.Auditor, HttpStatusCode.Forbidden)]
    public async Task CreateCompanyConnector_RequiresSlotConfigWrite(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.PostAsJsonAsync("/api/connectors", NewConnector("Company"));
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [TestCase(BuiltInRoles.User)]
    [TestCase(BuiltInRoles.Auditor)]
    public async Task CreatePersonalConnector_IsAllowedForAnyAuthenticatedPrincipal(string role)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.PostAsJsonAsync("/api/connectors", NewConnector("Personal"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "Connecting your own personal account needs no elevated role.");
    }

    // --- Configuration sharing: personal is self-owned; company + management need the permission ---

    private static object NewConfiguration(string scope) =>
        new { name = $"cfg-{Guid.NewGuid():N}", workflowType = "wt", scope };

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.Auditor, HttpStatusCode.Forbidden)]
    public async Task CreateCompanyConfiguration_RequiresConfigurationManage(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.PostAsJsonAsync("/api/configurations", NewConfiguration("Company"));
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [TestCase(BuiltInRoles.User)]
    [TestCase(BuiltInRoles.Auditor)]
    public async Task CreatePersonalConfiguration_IsAllowedForAnyAuthenticatedPrincipal(string role)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.PostAsJsonAsync("/api/configurations", NewConfiguration("Personal"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "Saving a personal workflow needs no elevated role.");
    }

    [Test]
    public async Task PersonalConfiguration_IsInvisibleToOtherUsers_EverywhereItReads()
    {
        var owner = await ClientForRolesAsync(BuiltInRoles.User);
        var stranger = await ClientForRolesAsync(BuiltInRoles.User);
        var manager = await ClientForRolesAsync(BuiltInRoles.Operator);
        var created = await (await owner.PostAsJsonAsync("/api/configurations", NewConfiguration("Personal")))
            .Content.ReadFromJsonAsync<RunConfiguration>();

        var strangerGet = await stranger.GetAsync($"/api/configurations/{created!.Id}");
        var strangerList = await stranger.GetFromJsonAsync<PagedResult<RunConfiguration>>("/api/configurations");
        var strangerRun = await stranger.PostAsJsonAsync(
            $"/api/configurations/{created.Id}/run", (IReadOnlyDictionary<string, string>?)null);
        var managerList = await manager.GetFromJsonAsync<PagedResult<RunConfiguration>>("/api/configurations");
        var ownerGet = await owner.GetAsync($"/api/configurations/{created.Id}");

        Assert.Multiple(() =>
        {
            Assert.That(strangerGet.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
                "An invisible personal configuration reads as not-found, not as forbidden.");
            Assert.That(strangerList!.Items.Any(c => c.Id == created.Id), Is.False,
                "Query filters other users' personal configurations out.");
            Assert.That(strangerRun.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
                "What you cannot see you cannot run.");
            Assert.That(managerList!.Items.Any(c => c.Id == created.Id), Is.True,
                "A configuration manager sees every configuration.");
            Assert.That(ownerGet.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public async Task PersonalConfiguration_OwnerManagesIt_AStrangerCannot_AGrantAdmits()
    {
        var owner = await ClientForRolesAsync(BuiltInRoles.User);
        var other = await ClientForRolesAsync(BuiltInRoles.User);
        var otherId = (await other.GetFromJsonAsync<CurrentPrincipal>("/auth/me"))!.PrincipalId;
        var created = await (await owner.PostAsJsonAsync("/api/configurations", NewConfiguration("Personal")))
            .Content.ReadFromJsonAsync<RunConfiguration>();

        var strangerUpdate = await other.PutAsJsonAsync(
            $"/api/configurations/{created!.Id}", new { name = "hijacked" });
        var strangerGrants = await other.PutAsJsonAsync(
            $"/api/configurations/{created.Id}/grants",
            new { grants = new[] { new { kind = "Principal", id = otherId.ToString("D") } } });
        var strangerDelete = await other.DeleteAsync($"/api/configurations/{created.Id}");

        var ownerUpdate = await owner.PutAsJsonAsync(
            $"/api/configurations/{created.Id}", new { name = "renamed by owner" });
        var ownerGrant = await owner.PutAsJsonAsync(
            $"/api/configurations/{created.Id}/grants",
            new { grants = new[] { new { kind = "Principal", id = otherId.ToString("D") } } });
        var grantedGet = await other.GetAsync($"/api/configurations/{created.Id}");
        var grantedRun = await other.PostAsJsonAsync(
            $"/api/configurations/{created.Id}/run", (IReadOnlyDictionary<string, string>?)null);
        var ownerDelete = await owner.DeleteAsync($"/api/configurations/{created.Id}");

        Assert.Multiple(() =>
        {
            Assert.That(strangerUpdate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(strangerGrants.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(strangerDelete.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(ownerUpdate.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "The owner edits without workflow-configuration.manage.");
            Assert.That(ownerGrant.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(grantedGet.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "A principal grant makes the configuration visible.");
            Assert.That(grantedRun.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "A granted principal may run the shared workflow.");
            Assert.That(ownerDelete.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        });
    }

    // --- Roles + sharing directory: read-only vocabulary for every authenticated principal ---

    [Test]
    public async Task RolesAndSharingDirectory_AreReadableByAPlainUser()
    {
        var client = await ClientForRolesAsync(BuiltInRoles.User);

        var roles = await client.GetFromJsonAsync<List<RoleDto>>("/api/roles");
        var subjects = await client.GetFromJsonAsync<SharingSubjects>("/api/directory/subjects");

        Assert.Multiple(() =>
        {
            Assert.That(roles!.Select(r => r.Name), Is.EquivalentTo(
                new[] { "Administrator", "Operator", "User", "Auditor" }));
            Assert.That(roles.Single(r => r.Name == "Operator").Permissions,
                Does.Contain("workflow-configuration.manage"));
            Assert.That(subjects!.Principals, Is.Not.Empty,
                "The picker directory lists principals (ids + display names only).");
        });
    }

    // --- Triggering: workflow.trigger → User and above, never Auditor ---

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Auditor, HttpStatusCode.Forbidden)]
    public async Task TriggerRun_RequiresWorkflowTrigger(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.PostAsJsonAsync("/api/runs",
            new { workflowType = "wt", packageUri = "docker://img" });
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    // --- Deny-by-default: an unroled principal is authenticated but powerless ---

    [Test]
    public async Task UnroledPrincipal_IsAuthenticated_ButDeniedEverySensitiveAction()
    {
        var client = await ClientForRolesAsync();

        var list = await client.GetAsync("/api/runs");
        var administer = await client.PostAsJsonAsync("/api/groups", new { name = "denied" });
        var configure = await client.PostAsJsonAsync("/api/connectors", NewConnector("Company"));
        var trigger = await client.PostAsJsonAsync("/api/runs",
            new { workflowType = "wt", packageUri = "docker://img" });

        Assert.Multiple(() =>
        {
            Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Reading runs needs only authentication.");
            Assert.That(administer.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(configure.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(trigger.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }

    // --- Groups: a role gained through membership authorizes at the HTTP layer ---

    [Test]
    public async Task RoleGainedThroughGroupMembership_AuthorizesAtTheHttpLayer()
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var groups = Factory.Services.GetRequiredService<GroupDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}", "Service");

        var group = await groups.CreateAsync($"ops-{Guid.NewGuid():N}");
        await groups.AddMemberAsync(group.Id, principal.Id);
        await groups.AssignRoleAsync(group.Id, BuiltInRoles.Operator);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var response = await client.PostAsJsonAsync("/api/connectors", NewConnector("Company"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "Operator gained via group membership must grant slot-config.write end to end.");
    }

    // --- Authentication gate + secret non-disclosure ---

    [TestCase("/api/groups")]
    [TestCase("/api/connectors")]
    [TestCase("/api/configurations")]
    [TestCase("/api/identity/group-mappings")]
    public async Task Anonymous_IsDenied_OnProtectedEndpoints(string path)
        => Assert.That((await CreateAnonymousClient().GetAsync(path)).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));

    [Test]
    public async Task ConnectorRead_DisclosesSettingKeys_NeverSecretValues()
    {
        var admin = CreateClient();
        var created = await (await admin.PostAsJsonAsync("/api/connectors",
                new { name = $"c-{Guid.NewGuid():N}", providerType = "github", settings = new { token = "supersecret-value" } }))
            .Content.ReadFromJsonAsync<Connector>();

        var raw = await (await admin.GetAsync($"/api/connectors/{created!.Id}")).Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(raw, Does.Contain("token"), "The setting key is safe to disclose.");
            Assert.That(raw, Does.Not.Contain("supersecret-value"), "The secret value must never leave the Core.");
        });
    }
}

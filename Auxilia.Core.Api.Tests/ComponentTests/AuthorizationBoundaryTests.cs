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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Component tests for the Core principal-administration API (principal.administer, mirrors the
/// groups admin): list/filter, create human + AI/service principals (API key returned once),
/// assign/revoke a Direct role without disturbing group-sourced roles, disable, and the auth gate.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class PrincipalApiTests : CoreApiComponentTestBase
{
    private ICoreClient Core => new CoreClient(CreateClient());

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

    [Test]
    public async Task CreateHuman_AppearsInList_WithNoRoles()
    {
        var core = Core;
        var created = await core.CreateHumanPrincipalAsync(
            new CreateHumanPrincipalRequest($"Ada {Guid.NewGuid():N}", $"ada-{Guid.NewGuid():N}", "pw-secret"));

        Assert.Multiple(() =>
        {
            Assert.That(created.Kind, Is.EqualTo("Human"));
            Assert.That(created.Status, Is.EqualTo("Active"));
            Assert.That(created.Roles, Is.Empty, "A new principal is deny-by-default.");
        });

        var page = await core.QueryPrincipalsAsync(new PrincipalQuery(Kind: "Human", Search: created.DisplayName));
        Assert.That(page.Items.Any(p => p.Id == created.Id), Is.True);
    }

    [Test]
    public async Task CreateAiPrincipal_ReturnsApiKeyOnce_AndTheKeyAuthenticates()
    {
        var core = Core;
        var result = await core.CreateApiKeyPrincipalAsync(new CreateApiKeyPrincipalRequest($"bot-{Guid.NewGuid():N}"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Principal.Kind, Is.EqualTo("AiAgent"));
            Assert.That(result.ApiKey, Is.Not.Empty);
            Assert.That(result.ApiKey, Does.StartWith("aux_"));
        });

        // The key works, and it is never disclosed again by a read.
        var keyed = Factory.CreateClient();
        keyed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.ApiKey);
        var me = await keyed.GetFromJsonAsync<CurrentPrincipal>("/auth/me");
        Assert.That(me!.PrincipalId, Is.EqualTo(result.Principal.Id));

        var raw = await (await CreateClient().GetAsync($"/api/principals/{result.Principal.Id}")).Content.ReadAsStringAsync();
        Assert.That(raw, Does.Not.Contain(result.ApiKey), "The API key is write-only after creation.");
    }

    [Test]
    public async Task AssignThenRevokeDirectRole_LeavesGroupSourcedRoleUntouched()
    {
        var core = Core;
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var groups = Factory.Services.GetRequiredService<GroupDirectory>();

        var (principal, _) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}", "Service");
        var group = await groups.CreateAsync($"ops-{Guid.NewGuid():N}");
        await groups.AddMemberAsync(group.Id, principal.Id);
        await groups.AssignRoleAsync(group.Id, BuiltInRoles.Operator);

        // Assign a Direct role, then confirm both the Direct and the Group role are surfaced.
        await core.AssignPrincipalRoleAsync(principal.Id, new AssignRoleRequest(BuiltInRoles.User));
        var afterAssign = await core.GetPrincipalAsync(principal.Id);
        Assert.Multiple(() =>
        {
            Assert.That(afterAssign!.Roles.Any(r => r is { RoleName: "User", Source: "Direct" }), Is.True);
            Assert.That(afterAssign.Roles.Any(r => r is { RoleName: "Operator", Source: "Group" }), Is.True);
        });

        // Revoking the Direct role must not remove the group-sourced Operator.
        await core.RevokePrincipalRoleAsync(principal.Id, BuiltInRoles.User);
        var afterRevoke = await core.GetPrincipalAsync(principal.Id);
        Assert.Multiple(() =>
        {
            Assert.That(afterRevoke!.Roles.Any(r => r.RoleName == "User"), Is.False);
            Assert.That(afterRevoke.Roles.Any(r => r is { RoleName: "Operator", Source: "Group" }), Is.True,
                "A role held through a group is never a Direct assignment and must survive revocation.");
        });
    }

    [Test]
    public async Task AssignUnknownRole_Returns400()
    {
        var client = CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/principals",
                new CreateHumanPrincipalRequest($"H {Guid.NewGuid():N}", $"u-{Guid.NewGuid():N}", "pw")))
            .Content.ReadFromJsonAsync<PrincipalDto>();
        var response = await client.PostAsJsonAsync(
            $"/api/principals/{created!.Id}/roles", new AssignRoleRequest("Wizard"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Disable_PrincipalCanNoLongerAuthenticate()
    {
        var core = Core;
        var result = await core.CreateApiKeyPrincipalAsync(new CreateApiKeyPrincipalRequest($"bot-{Guid.NewGuid():N}"));
        await core.AssignPrincipalRoleAsync(result.Principal.Id, new AssignRoleRequest(BuiltInRoles.Operator));

        var keyed = Factory.CreateClient();
        keyed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.ApiKey);
        Assert.That((await keyed.GetAsync("/auth/me")).StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "Enabled principal authenticates.");

        await core.SetPrincipalEnabledAsync(result.Principal.Id, new SetPrincipalEnabledRequest(false));

        Assert.That((await keyed.GetAsync("/auth/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "A disabled principal can no longer authenticate.");

        // And re-enabling restores authentication.
        await core.SetPrincipalEnabledAsync(result.Principal.Id, new SetPrincipalEnabledRequest(true));
        Assert.That((await keyed.GetAsync("/auth/me")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task SetEnabled_UnknownPrincipal_Returns404()
    {
        var response = await CreateClient().PostAsJsonAsync(
            $"/api/principals/{Guid.NewGuid()}/enabled", new SetPrincipalEnabledRequest(false));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // --- Auth gate: principal.administer → Administrator only ---

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.Auditor, HttpStatusCode.Forbidden)]
    public async Task ListPrincipals_IsAdministratorOnly(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        Assert.That((await client.GetAsync("/api/principals")).StatusCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task CreatePrincipal_NonAdmin_Returns403()
    {
        var client = await ClientForRolesAsync(BuiltInRoles.Operator);
        var response = await client.PostAsJsonAsync("/api/principals",
            new CreateHumanPrincipalRequest("Nope", $"u-{Guid.NewGuid():N}", "pw"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Principals_Anonymous_Returns401()
        => Assert.That((await CreateAnonymousClient().GetAsync("/api/principals")).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
}

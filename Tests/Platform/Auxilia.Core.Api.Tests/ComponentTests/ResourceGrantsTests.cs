using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Access enforcement for platform resources beyond connectors, over REST: the workflow-type
/// ACCESS LIST (the Policy Engine's per-(type, action) exclusive entries, previously
/// MCP-administered only) gates who may dispatch a type, and an ENVIRONMENT LAYER with grants
/// gates who may bind it into a run — both enforced at dispatch.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ResourceGrantsTests : CoreApiComponentTestBase
{
    private async Task<(HttpClient Client, Guid PrincipalId)> UserClientAsync()
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}");
        await directory.AssignRoleAsync(principal.Id, BuiltInRoles.User);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return (client, principal.Id);
    }

    private async Task<string> RegisterTypeAsync(string type)
    {
        await RegisterActiveTypeAsync(CreateClient(), type, "docker://img");
        return type;
    }

    [Test]
    public async Task WorkflowTypeAccessList_OverRest_GatesDispatchExclusively()
    {
        var admin = CreateClient();
        var type = await RegisterTypeAsync($"acl-wt-{Guid.NewGuid():N}");
        var (granted, grantedId) = await UserClientAsync();
        var (stranger, _) = await UserClientAsync();

        // No entries + restricted platform default: administrators only.
        Assert.That((await stranger.PostAsJsonAsync("/api/runs", new { workflowType = type })).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden),
            "an ungranted type is admin-only under the restricted default");

        // Opened, the role-derived workflow.trigger permission decides.
        await OpenDefaultResourceAccessAsync();
        Assert.That((await stranger.PostAsJsonAsync("/api/runs", new { workflowType = type })).StatusCode,
            Is.EqualTo(HttpStatusCode.OK), "a type without access entries is role-governed once opened");

        // The FIRST entry makes the list the EXCLUSIVE grant source for (type, workflow.trigger).
        var grant = await admin.PostAsJsonAsync($"/api/workflow-types/{type}/access/grant",
            new { action = PermissionActions.WorkflowTrigger, principalId = grantedId });
        Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var entries = await admin.GetFromJsonAsync<List<WorkflowTypeAccessEntryDto>>(
            $"/api/workflow-types/{type}/access");
        Assert.That(entries!.Single().PrincipalId, Is.EqualTo(grantedId), "the entry lists over REST");

        Assert.Multiple(async () =>
        {
            Assert.That((await granted.PostAsJsonAsync("/api/runs", new { workflowType = type })).StatusCode,
                Is.EqualTo(HttpStatusCode.OK), "the listed principal dispatches");
            Assert.That((await stranger.PostAsJsonAsync("/api/runs", new { workflowType = type })).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden),
                "an unlisted principal is refused — the list is exclusive once populated");
        });

        // Revoking the last entry returns the type to role-governed access.
        await admin.PostAsJsonAsync($"/api/workflow-types/{type}/access/revoke",
            new { action = PermissionActions.WorkflowTrigger, principalId = grantedId });
        Assert.That((await stranger.PostAsJsonAsync("/api/runs", new { workflowType = type })).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task WorkflowTypeAccessList_ValidatesTheSubject()
    {
        var admin = CreateClient();
        var type = await RegisterTypeAsync($"acl-val-{Guid.NewGuid():N}");

        Assert.Multiple(async () =>
        {
            Assert.That((await admin.PostAsJsonAsync($"/api/workflow-types/{type}/access/grant",
                    new { action = PermissionActions.WorkflowTrigger })).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest), "no subject is refused");
            Assert.That((await admin.PostAsJsonAsync($"/api/workflow-types/{type}/access/grant",
                    new { action = PermissionActions.WorkflowTrigger, roleName = "NoSuchRole" })).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest), "an unknown role is refused");
            Assert.That((await admin.PostAsJsonAsync($"/api/workflow-types/{type}/access/grant",
                    new
                    {
                        action = PermissionActions.WorkflowTrigger,
                        roleName = "User",
                        principalId = Guid.NewGuid()
                    })).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest), "two subjects at once are refused");
        });
    }

    [Test]
    public async Task EnvironmentLayerGrants_GateTheBinding_AtDispatch()
    {
        var admin = CreateClient();
        // This fixture exercises the GRANT mechanics; the restricted default has its own test below.
        await OpenDefaultResourceAccessAsync();
        var type = await RegisterTypeAsync($"env-wt-{Guid.NewGuid():N}");
        var layer = $"layer-{Guid.NewGuid():N}";
        await admin.PostAsJsonAsync("/api/environment-layers",
            new { providerType = layer, description = "test layer", setupScript = "echo hi" });
        var (granted, grantedId) = await UserClientAsync();
        var (stranger, _) = await UserClientAsync();

        object RunWithLayer() => new
        {
            workflowType = type,
            slotBindings = new[] { new { slotName = "environment", providerType = layer } }
        };

        Assert.That((await stranger.PostAsJsonAsync("/api/runs", RunWithLayer())).StatusCode,
            Is.EqualTo(HttpStatusCode.OK), "a layer without grants binds freely");

        var setResponse = await admin.PutAsJsonAsync($"/api/environment-layers/{layer}/grants",
            new { grants = new[] { new { kind = "Principal", id = grantedId.ToString("D") } } });
        Assert.That(setResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var dto = await setResponse.Content.ReadFromJsonAsync<EnvironmentLayerDto>();
        Assert.That(dto!.Grants, Has.Count.EqualTo(1), "the grants round-trip on the DTO");

        Assert.Multiple(async () =>
        {
            Assert.That((await granted.PostAsJsonAsync("/api/runs", RunWithLayer())).StatusCode,
                Is.EqualTo(HttpStatusCode.OK), "the granted principal binds the layer");
            Assert.That((await stranger.PostAsJsonAsync("/api/runs", RunWithLayer())).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden), "an ungranted principal is refused at dispatch");
            Assert.That((await stranger.PostAsJsonAsync("/api/runs", new { workflowType = type })).StatusCode,
                Is.EqualTo(HttpStatusCode.OK), "the same principal runs fine WITHOUT the gated layer");
        });
    }

    [Test]
    public async Task ProviderGrants_GateThePluginBinding_AtDispatch()
    {
        var admin = CreateClient();
        await OpenDefaultResourceAccessAsync();
        var type = await RegisterTypeAsync($"prov-wt-{Guid.NewGuid():N}");
        var provider = $"provider-{Guid.NewGuid():N}";
        await admin.PostAsJsonAsync("/api/provider-catalog", new
        {
            providerType = provider, category = "test", description = "gated provider",
            contracts = new[] { "ITest" }, settings = Array.Empty<object>()
        });
        await admin.PostAsJsonAsync($"/api/provider-catalog/{provider}/availability", new { available = true });
        var (granted, grantedId) = await UserClientAsync();
        var (stranger, _) = await UserClientAsync();

        object RunWithProvider() => new
        {
            workflowType = type,
            slotBindings = new[] { new { slotName = "test-slot", providerType = provider } }
        };

        Assert.That((await stranger.PostAsJsonAsync("/api/runs", RunWithProvider())).StatusCode,
            Is.EqualTo(HttpStatusCode.OK), "a provider without grants binds freely");

        var set = await admin.PutAsJsonAsync($"/api/provider-catalog/{provider}/grants",
            new { grants = new[] { new { kind = "Principal", id = grantedId.ToString("D") } } });
        Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var entry = await set.Content.ReadFromJsonAsync<ProviderCatalogEntry>();
        Assert.That(entry!.Grants, Has.Count.EqualTo(1), "the grants round-trip on the entry");

        Assert.Multiple(async () =>
        {
            Assert.That((await granted.PostAsJsonAsync("/api/runs", RunWithProvider())).StatusCode,
                Is.EqualTo(HttpStatusCode.OK), "the granted principal binds the provider");
            Assert.That((await stranger.PostAsJsonAsync("/api/runs", RunWithProvider())).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden), "an ungranted principal is refused at dispatch");
        });
    }

    [Test]
    public async Task RestrictedDefault_MakesUngrantedResourcesAdminOnly_AndGrantsOpenThem()
    {
        var admin = CreateClient();
        var type = await RegisterTypeAsync($"restricted-wt-{Guid.NewGuid():N}");
        var layer = $"restricted-layer-{Guid.NewGuid():N}";
        await admin.PostAsJsonAsync("/api/environment-layers",
            new { providerType = layer, description = "restricted layer", setupScript = "echo hi" });
        var (user, userId) = await UserClientAsync();

        object RunWithLayer() => new
        {
            workflowType = type,
            slotBindings = new[] { new { slotName = "environment", providerType = layer } }
        };

        Assert.Multiple(async () =>
        {
            Assert.That((await user.PostAsJsonAsync("/api/runs", new { workflowType = type })).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden),
                "an ungranted TYPE is admin-only under the restricted default");
            Assert.That((await admin.PostAsJsonAsync("/api/runs", RunWithLayer())).StatusCode,
                Is.EqualTo(HttpStatusCode.OK),
                "the administrator dispatches an ungranted type + layer regardless");
        });

        // An access-list entry admits the user to the TYPE…
        await admin.PostAsJsonAsync($"/api/workflow-types/{type}/access/grant",
            new { action = PermissionActions.WorkflowTrigger, principalId = userId });
        Assert.Multiple(async () =>
        {
            Assert.That((await user.PostAsJsonAsync("/api/runs", new { workflowType = type })).StatusCode,
                Is.EqualTo(HttpStatusCode.OK), "the access-list entry admits the user to the type");
            Assert.That((await user.PostAsJsonAsync("/api/runs", RunWithLayer())).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden),
                "…but the ungranted LAYER stays admin-only under the restricted default");
        });

        // …and a layer grant opens the layer for the user too.
        await admin.PutAsJsonAsync($"/api/environment-layers/{layer}/grants",
            new { grants = new[] { new { kind = "Principal", id = userId.ToString("D") } } });
        Assert.That((await user.PostAsJsonAsync("/api/runs", RunWithLayer())).StatusCode,
            Is.EqualTo(HttpStatusCode.OK), "the layer grant admits the user");
    }

    [Test]
    public async Task GrantEndpoints_RequireTheirManagePermissions()
    {
        var type = await RegisterTypeAsync($"perm-wt-{Guid.NewGuid():N}");
        var (user, userId) = await UserClientAsync();

        Assert.Multiple(async () =>
        {
            Assert.That((await user.GetAsync($"/api/workflow-types/{type}/access")).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden), "the access list needs policy.administer");
            Assert.That((await user.PostAsJsonAsync($"/api/workflow-types/{type}/access/grant",
                    new { action = PermissionActions.WorkflowTrigger, principalId = userId })).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden), "access mutations need policy.administer");
            Assert.That((await user.PutAsJsonAsync("/api/environment-layers/any/grants",
                    new { grants = new[] { new { kind = "Principal", id = userId.ToString("D") } } })).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden), "layer grants need provider-catalog.manage");
        });
    }
}

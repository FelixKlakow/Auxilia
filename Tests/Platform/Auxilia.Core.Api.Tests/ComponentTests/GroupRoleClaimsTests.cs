using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// A role held through a first-class group must reach the session's claims: <c>/auth/me</c>
/// reports it, and the claims-based visibility filters (configuration listing) honour it —
/// otherwise a group-granted manager sees an empty role set and a filtered list while the Policy
/// Engine would have allowed every action.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class GroupRoleClaimsTests : CoreApiComponentTestBase
{
    private async Task<HttpClient> GroupMemberWithGroupRoleAsync(string role)
    {
        var admin = CreateClient();
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (member, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}");

        var group = (await (await admin.PostAsJsonAsync(
                "/api/groups", new CreateGroupRequest($"g-{Guid.NewGuid():N}")))
            .Content.ReadFromJsonAsync<GroupDto>())!;
        Assert.That((await admin.PostAsJsonAsync(
                $"/api/groups/{group.Id}/members", new AddGroupMemberRequest(member.Id))).IsSuccessStatusCode);
        Assert.That((await admin.PostAsJsonAsync(
                $"/api/groups/{group.Id}/roles", new AssignGroupRoleRequest(role))).IsSuccessStatusCode);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    [Test]
    public async Task AuthMe_ReportsAGroupHeldRole_AndItsPermissions()
    {
        var member = await GroupMemberWithGroupRoleAsync(BuiltInRoles.Operator);

        var me = await member.GetFromJsonAsync<CurrentPrincipal>("/auth/me");

        Assert.Multiple(() =>
        {
            Assert.That(me!.Roles, Does.Contain(BuiltInRoles.Operator));
            Assert.That(me.Permissions, Does.Contain(PermissionActions.WorkflowConfigurationManage));
        });
    }

    [Test]
    public async Task ConfigurationListing_HonoursAGroupHeldManagerRole()
    {
        var member = await GroupMemberWithGroupRoleAsync(BuiltInRoles.Operator);
        var configurations = Factory.Services.GetRequiredService<RunConfigurationService>();
        var someoneElsesPersonal = await configurations.CreateAsync(new CreateRunConfiguration(
                "cfg-" + Guid.NewGuid().ToString("N"), "wf", Scope: ResourceScope.Personal),
            ownerPrincipalId: Guid.NewGuid(), CancellationToken.None);

        var page = await member.GetFromJsonAsync<PagedResult<RunConfiguration>>("/api/configurations");

        Assert.That(page!.Items.Select(c => c.Id), Does.Contain(someoneElsesPersonal.Id),
            "a group-held manager role makes every personal configuration visible, like a direct one");
    }
}

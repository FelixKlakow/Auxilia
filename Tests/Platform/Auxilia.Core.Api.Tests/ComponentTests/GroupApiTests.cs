using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>Component tests for the Core group-administration API (identity, admin-authorized).</summary>
[TestFixture]
[Category("Component")]
public sealed class GroupApiTests : CoreApiComponentTestBase
{
    [Test]
    public async Task CreateGroup_AddMember_AssignRole_ReflectedInList()
    {
        var client = CreateClient();

        var create = await client.PostAsJsonAsync("/api/groups", new CreateGroupRequest("devs", "Developers"));
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var group = await create.Content.ReadFromJsonAsync<GroupDto>();
        Assert.That(group, Is.Not.Null);

        var principalId = Guid.NewGuid();
        var addMember = await client.PostAsJsonAsync(
            $"/api/groups/{group!.Id}/members", new AddGroupMemberRequest(principalId));
        Assert.That(addMember.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        var assignRole = await client.PostAsJsonAsync(
            $"/api/groups/{group.Id}/roles", new AssignGroupRoleRequest("Operator"));
        Assert.That(assignRole.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        var list = await client.GetFromJsonAsync<List<GroupDto>>("/api/groups");
        var found = list!.Single(g => g.Id == group.Id);
        Assert.That(found.Members, Does.Contain(principalId));
        Assert.That(found.Roles, Does.Contain("Operator"));
    }

    [Test]
    public async Task CreateGroup_Unauthenticated_Returns401()
    {
        var response = await CreateAnonymousClient()
            .PostAsJsonAsync("/api/groups", new CreateGroupRequest("x"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AssignUnknownRole_Returns400()
    {
        var client = CreateClient();
        var group = await (await client.PostAsJsonAsync("/api/groups", new CreateGroupRequest("g")))
            .Content.ReadFromJsonAsync<GroupDto>();
        var response = await client.PostAsJsonAsync(
            $"/api/groups/{group!.Id}/roles", new AssignGroupRoleRequest("NotARole"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // --- Step-up elevation on the group path to Administrator (the group-path bypass fix) ---

    private async Task<HttpClient> ElevatedAdminAsync()
    {
        var admin = CreateClient();
        var stepUp = await admin.PostAsJsonAsync("/auth/step-up", new StepUpRequest(TestApiKey));
        var ticket = await stepUp.Content.ReadFromJsonAsync<ElevationTicket>();
        admin.DefaultRequestHeaders.Add("X-Auxilia-Elevation", ticket!.Token);
        return admin;
    }

    private async Task<GroupDto> CreateGroupAsync(HttpClient client)
        => (await (await client.PostAsJsonAsync(
                "/api/groups", new CreateGroupRequest($"g-{Guid.NewGuid():N}")))
            .Content.ReadFromJsonAsync<GroupDto>())!;

    [Test]
    public async Task AssignAdministratorGroupRole_WithoutElevation_IsRefused()
    {
        var admin = CreateClient();
        var group = await CreateGroupAsync(admin);

        var response = await admin.PostAsJsonAsync(
            $"/api/groups/{group.Id}/roles", new AssignGroupRoleRequest("Administrator"));

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("elevation-required"),
                "the group path to Administrator is elevation-gated exactly like the direct grant");
        });
    }

    [Test]
    public async Task AssignAdministratorGroupRole_WithElevation_Succeeds()
    {
        var admin = await ElevatedAdminAsync();
        var group = await CreateGroupAsync(admin);

        var response = await admin.PostAsJsonAsync(
            $"/api/groups/{group.Id}/roles", new AssignGroupRoleRequest("Administrator"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task AddMember_ToAdministratorHoldingGroup_RequiresElevation()
    {
        var elevated = await ElevatedAdminAsync();
        var group = await CreateGroupAsync(elevated);
        Assert.That((await elevated.PostAsJsonAsync(
                $"/api/groups/{group.Id}/roles", new AssignGroupRoleRequest("Administrator"))).StatusCode,
            Is.EqualTo(HttpStatusCode.Accepted));

        // A fresh, non-elevated admin client: admitting a member IS an admin grant to that member.
        var refused = await CreateClient().PostAsJsonAsync(
            $"/api/groups/{group.Id}/members", new AddGroupMemberRequest(Guid.NewGuid()));
        Assert.Multiple(async () =>
        {
            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await refused.Content.ReadAsStringAsync(), Does.Contain("elevation-required"));
        });

        // The elevated caller may admit the member.
        var admitted = await elevated.PostAsJsonAsync(
            $"/api/groups/{group.Id}/members", new AddGroupMemberRequest(Guid.NewGuid()));
        Assert.That(admitted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task AddMember_ToANormalGroup_NeedsNoElevation()
    {
        var admin = CreateClient();
        var group = await CreateGroupAsync(admin);
        Assert.That((await admin.PostAsJsonAsync(
                $"/api/groups/{group.Id}/roles", new AssignGroupRoleRequest("Operator"))).StatusCode,
            Is.EqualTo(HttpStatusCode.Accepted), "non-admin group roles need no elevation");

        var response = await admin.PostAsJsonAsync(
            $"/api/groups/{group.Id}/members", new AddGroupMemberRequest(Guid.NewGuid()));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }
}

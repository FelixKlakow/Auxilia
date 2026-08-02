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
}

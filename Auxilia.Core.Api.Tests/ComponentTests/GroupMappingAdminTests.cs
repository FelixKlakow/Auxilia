using System.Net;
using System.Net.Http.Json;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The administrator surface for directory group → role mappings (REST). MCP parity is covered by the
/// same <see cref="Auxilia.Core.Api.Mcp.CoreMcpTools"/> service layer these endpoints call.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class GroupMappingAdminTests : CoreApiComponentTestBase
{
    private sealed record GroupMappingDto(Guid Id, string IdentityProvider, string GroupClaim, string RoleName);

    private static readonly object OpsMapping =
        new { identityProvider = "entra", groupClaim = "group-ops", roleName = "Operator" };

    [Test]
    public async Task Create_ThenList_ReturnsTheMapping()
    {
        var admin = CreateClient();

        var created = await admin.PostAsJsonAsync("/api/identity/group-mappings", OpsMapping);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await admin.GetFromJsonAsync<GroupMappingDto[]>("/api/identity/group-mappings");
        Assert.That(list, Has.One.Matches<GroupMappingDto>(m =>
            m is { IdentityProvider: "entra", GroupClaim: "group-ops", RoleName: "Operator" }));
    }

    [Test]
    public async Task Create_WithUnknownRole_Returns400()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/identity/group-mappings",
            new { identityProvider = "entra", groupClaim = "group-ops", roleName = "Wizard" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Delete_RemovesTheMapping()
    {
        var admin = CreateClient();
        var created = await (await admin.PostAsJsonAsync("/api/identity/group-mappings", OpsMapping))
            .Content.ReadFromJsonAsync<GroupMappingDto>();

        var deleted = await admin.DeleteAsync($"/api/identity/group-mappings/{created!.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await admin.GetFromJsonAsync<GroupMappingDto[]>("/api/identity/group-mappings"), Is.Empty);
    }

    [Test]
    public async Task Anonymous_IsRejected()
    {
        var response = await CreateAnonymousClient().GetAsync("/api/identity/group-mappings");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}

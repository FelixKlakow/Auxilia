using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// First-class repository resources over REST: personal creation is self-serve, settings are
/// returned on reads (non-secret), edits are owner-or-manager, and visibility follows the
/// connector sharing model (a personal repository is invisible to strangers).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class WorkspaceAdminTests : CoreApiComponentTestBase
{
    private static CreateWorkspaceResource MainRepo(string scope = ResourceScope.Personal) => new(
        "Main product repo", "git-repository",
        new Dictionary<string, string>
        {
            ["CloneUrl"] = "https://git.example.test/product.git",
            ["Branch"] = "main",
            ["SetupScript"] = "dotnet restore",
        },
        Scope: scope);

    private async Task<HttpClient> UserClientAsync()
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}");
        await directory.AssignRoleAsync(principal.Id, "User");
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    [Test]
    public async Task Create_Read_Update_Delete_RoundTrips_WithSettings()
    {
        var admin = CreateClient();

        var created = await (await admin.PostAsJsonAsync("/api/workspaces", MainRepo()))
            .Content.ReadFromJsonAsync<WorkspaceResource>();
        Assert.That(created!.Settings["SetupScript"], Is.EqualTo("dotnet restore"),
            "settings are non-secret and round-trip on reads");

        var updated = await (await admin.PutAsJsonAsync($"/api/workspaces/{created.Id}",
                new UpdateWorkspaceResource("Main product repo", new Dictionary<string, string>
                {
                    ["CloneUrl"] = "https://git.example.test/product.git",
                    ["Branch"] = "develop",
                    ["SetupScript"] = "dotnet restore && npm ci",
                })))
            .Content.ReadFromJsonAsync<WorkspaceResource>();
        Assert.Multiple(() =>
        {
            Assert.That(updated!.Settings["Branch"], Is.EqualTo("develop"));
            Assert.That(updated.Settings["SetupScript"], Is.EqualTo("dotnet restore && npm ci"),
                "edit once — every configuration referencing the id gets the new script");
        });

        var deleted = await admin.DeleteAsync($"/api/workspaces/{created.Id}");
        Assert.Multiple(async () =>
        {
            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await admin.GetAsync($"/api/workspaces/{created.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task PersonalRepository_IsInvisibleToStrangers_UntilGranted()
    {
        var owner = await UserClientAsync();
        var stranger = await UserClientAsync();

        var created = await (await owner.PostAsJsonAsync("/api/workspaces", MainRepo()))
            .Content.ReadFromJsonAsync<WorkspaceResource>();

        var ownList = await owner.GetFromJsonAsync<IReadOnlyList<WorkspaceResource>>("/api/workspaces");
        var strangerList = await stranger.GetFromJsonAsync<IReadOnlyList<WorkspaceResource>>("/api/workspaces");
        Assert.Multiple(() =>
        {
            Assert.That(ownList!.Select(r => r.Id), Does.Contain(created!.Id));
            Assert.That(strangerList!.Select(r => r.Id), Does.Not.Contain(created.Id),
                "a personal repository is invisible to strangers");
        });
    }

    [Test]
    public async Task Update_ByAStranger_IsForbidden()
    {
        var owner = await UserClientAsync();
        var stranger = await UserClientAsync();
        var created = await (await owner.PostAsJsonAsync("/api/workspaces", MainRepo()))
            .Content.ReadFromJsonAsync<WorkspaceResource>();

        var response = await stranger.PutAsJsonAsync($"/api/workspaces/{created!.Id}",
            new UpdateWorkspaceResource("Hijacked", new Dictionary<string, string>()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "editing is owner-or-manager");
    }

    [Test]
    public async Task AvailableProviderCatalog_IsReadableByAnyUser_TheFullViewStaysGated()
    {
        var user = await UserClientAsync();

        var availableSlice = await user.GetAsync("/api/provider-catalog?available=true");
        var fullView = await user.GetAsync("/api/provider-catalog");

        Assert.Multiple(() =>
        {
            Assert.That(availableSlice.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "configuration editors (repository/connector forms) render from the available slice");
            Assert.That(fullView.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
                "the curation view (unavailable entries included) stays admin-gated");
        });
    }

    [Test]
    public async Task CompanyScope_WithoutTheManagePermission_IsForbidden()
    {
        var user = await UserClientAsync();

        var response = await user.PostAsJsonAsync("/api/workspaces", MainRepo(ResourceScope.Company));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}

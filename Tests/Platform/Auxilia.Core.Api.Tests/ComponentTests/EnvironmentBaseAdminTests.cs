using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The environment-base catalog: admin-managed (name, version) pairs — the configurable
/// vocabulary layers build on. Writes require provider-catalog.manage; a layer's base-version
/// pin must name a registered base version.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class EnvironmentBaseAdminTests : CoreApiComponentTestBase
{
    [Test]
    public async Task Upsert_StoresTheBase_NormalizedAndListed()
    {
        var admin = CreateClient();

        var created = await admin.PostAsJsonAsync("/api/environment-bases",
            new UpsertEnvironmentBase("  Linux ", " ubuntu-24.04 ", "Noble LTS"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var bases = await admin.GetFromJsonAsync<IReadOnlyList<EnvironmentBaseDto>>("/api/environment-bases");
        var entry = bases!.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Name, Is.EqualTo("linux"), "names are lowercased on write");
            Assert.That(entry.Version, Is.EqualTo("ubuntu-24.04"));
            Assert.That(entry.Description, Is.EqualTo("Noble LTS"));
        });
    }

    [Test]
    public async Task Upsert_SameNameDifferentVersions_AreDistinctEntries()
    {
        var admin = CreateClient();

        await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase("linux", "ubuntu-22.04"));
        await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase("linux", "ubuntu-24.04"));

        var bases = await admin.GetFromJsonAsync<IReadOnlyList<EnvironmentBaseDto>>("/api/environment-bases");
        Assert.That(bases!.Select(b => b.Version), Is.EqualTo(new[] { "ubuntu-22.04", "ubuntu-24.04" }),
            "one base name may exist in several versions");
    }

    [Test]
    public async Task List_WithSearch_FiltersBothCatalogs()
    {
        var admin = CreateClient();
        await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase("linux", "ubuntu-24.04", "Noble LTS"));
        await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase("windows", "server-2022"));
        await admin.PostAsJsonAsync("/api/environment-layers",
            new UpsertEnvironmentLayer("dotnet-10", ".NET SDK", "apt-get install -y dotnet-sdk-10.0"));
        await admin.PostAsJsonAsync("/api/environment-layers",
            new UpsertEnvironmentLayer("blender", "3D tooling", "apt-get install -y blender"));

        var byVersion = await admin.GetFromJsonAsync<IReadOnlyList<EnvironmentBaseDto>>(
            "/api/environment-bases?search=UBUNTU");
        var byDescription = await admin.GetFromJsonAsync<IReadOnlyList<EnvironmentBaseDto>>(
            "/api/environment-bases?search=noble");
        var layersByType = await admin.GetFromJsonAsync<IReadOnlyList<EnvironmentLayerDto>>(
            "/api/environment-layers?search=dotnet");
        var layersByDescription = await admin.GetFromJsonAsync<IReadOnlyList<EnvironmentLayerDto>>(
            "/api/environment-layers?search=3d tooling");
        var noMatch = await admin.GetFromJsonAsync<IReadOnlyList<EnvironmentLayerDto>>(
            "/api/environment-layers?search=nothing-here");

        Assert.Multiple(() =>
        {
            Assert.That(byVersion!.Single().Version, Is.EqualTo("ubuntu-24.04"), "search is case-insensitive");
            Assert.That(byDescription!.Single().Description, Is.EqualTo("Noble LTS"));
            Assert.That(layersByType!.Single().ProviderType, Is.EqualTo("dotnet-10"));
            Assert.That(layersByDescription!.Single().ProviderType, Is.EqualTo("blender"));
            Assert.That(noMatch, Is.Empty);
        });
    }

    [Test]
    public async Task Upsert_WithoutNameOrVersion_IsRejected()
    {
        var admin = CreateClient();

        Assert.Multiple(async () =>
        {
            Assert.That(
                (await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase(" ", "v1"))).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(
                (await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase("linux", " "))).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task Delete_RemovesExactlyThatVersion()
    {
        var admin = CreateClient();
        await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase("linux", "ubuntu-22.04"));
        await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase("linux", "ubuntu-24.04"));

        var deleted = await admin.DeleteAsync("/api/environment-bases/linux/ubuntu-22.04");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var bases = await admin.GetFromJsonAsync<IReadOnlyList<EnvironmentBaseDto>>("/api/environment-bases");
        Assert.That(bases!.Single().Version, Is.EqualTo("ubuntu-24.04"));
    }

    [Test]
    public async Task LayerUpsert_PinnedBaseVersion_MustBeRegistered()
    {
        var admin = CreateClient();

        var unpinnedUnknownBase = await admin.PostAsJsonAsync("/api/environment-layers",
            new UpsertEnvironmentLayer("dotnet-10", null, "apt-get install -y dotnet-sdk-10.0"));
        var pinnedUnregistered = await admin.PostAsJsonAsync("/api/environment-layers",
            new UpsertEnvironmentLayer("dotnet-11", null, "apt-get install -y dotnet-sdk-11.0",
                BaseVersion: "ubuntu-24.04"));

        await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase("linux", "ubuntu-24.04"));
        var pinnedRegistered = await admin.PostAsJsonAsync("/api/environment-layers",
            new UpsertEnvironmentLayer("dotnet-11", null, "apt-get install -y dotnet-sdk-11.0",
                BaseVersion: "ubuntu-24.04"));

        Assert.Multiple(() =>
        {
            Assert.That(unpinnedUnknownBase.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "an unpinned layer composes with any version — no catalog needed");
            Assert.That(pinnedUnregistered.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
                "a pin against a typo would silently never compose with anything");
            Assert.That(pinnedRegistered.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public async Task LayerUpsert_PinnedBaseVersion_RidesTheCatalogEntry()
    {
        var admin = CreateClient();
        await admin.PostAsJsonAsync("/api/environment-bases", new UpsertEnvironmentBase("linux", "ubuntu-24.04"));
        await admin.PostAsJsonAsync("/api/environment-layers",
            new UpsertEnvironmentLayer("dotnet-11", null, "apt-get install -y dotnet-sdk-11.0",
                BaseVersion: "ubuntu-24.04"));

        var catalog = await admin.GetFromJsonAsync<PagedResult<ProviderCatalogEntry>>("/api/provider-catalog");
        var entry = catalog!.Items.Single(e => e.ProviderType == "dotnet-11");
        Assert.Multiple(() =>
        {
            var baseRef = entry.EnvironmentBases!.Single();
            Assert.That(baseRef.Name, Is.EqualTo("linux"));
            Assert.That(baseRef.Version, Is.EqualTo("ubuntu-24.04"),
                "the pin must ride the catalog entry — dispatch checks read it there");
        });
    }

    [Test]
    public async Task Writes_WithoutTheCatalogPermission_AreForbidden()
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}");
        await directory.AssignRoleAsync(principal.Id, "User");
        var user = Factory.CreateClient();
        user.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var response = await user.PostAsJsonAsync("/api/environment-bases",
            new UpsertEnvironmentBase("linux", "ubuntu-24.04"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}

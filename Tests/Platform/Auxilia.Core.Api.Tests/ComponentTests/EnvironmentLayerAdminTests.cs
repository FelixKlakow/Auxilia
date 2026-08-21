using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Admin-managed environment layers: upserting stores the Dockerfile fragment AND maintains an
/// available provider-catalog entry (category "environment"); the runner reads fragment content
/// with a run's resolution token; writes require provider-catalog.manage.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class EnvironmentLayerAdminTests : CoreApiComponentTestBase
{
    private static UpsertEnvironmentLayer Blender(string? description = null) => new(
        "blender-4", description ?? "Blender 4 preinstalled",
        "apt-get update\napt-get install -y blender\n", Version: "4.2");

    [Test]
    public async Task Upsert_StoresTheLayer_AndPublishesAnAvailableCatalogEntry()
    {
        var admin = CreateClient();

        var created = await admin.PostAsJsonAsync("/api/environment-layers", Blender());
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var layer = await admin.GetFromJsonAsync<EnvironmentLayerDto>("/api/environment-layers/blender-4");
        Assert.Multiple(() =>
        {
            var variant = layer!.Variants.Single();
            Assert.That(variant.SetupScript, Does.Contain("blender"));
            Assert.That(variant.BaseEnvironment, Is.EqualTo("linux"));
            Assert.That(layer.Version, Is.EqualTo("4.2"));
        });

        var catalog = await admin.GetFromJsonAsync<PagedResult<ProviderCatalogEntry>>(
            "/api/provider-catalog?available=true");
        var entry = catalog!.Items.Single(e => e.ProviderType == "blender-4");
        Assert.Multiple(() =>
        {
            Assert.That(entry.Category, Is.EqualTo("environment"));
            Assert.That(entry.ComposesEnvironment, Is.True);
            Assert.That(entry.Available, Is.True, "the upsert is the admin's curation act");
        });
    }

    [Test]
    public async Task Upsert_WithoutASetupScript_IsRejected()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/environment-layers",
            new UpsertEnvironmentLayer("bad", null, "   "));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "the script IS how the environment gets ready");
    }

    [Test]
    public async Task Delete_RemovesLayerAndCatalogEntry()
    {
        var admin = CreateClient();
        await admin.PostAsJsonAsync("/api/environment-layers", Blender());

        var deleted = await admin.DeleteAsync("/api/environment-layers/blender-4");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Assert.Multiple(async () =>
        {
            Assert.That(
                (await admin.GetAsync("/api/environment-layers/blender-4")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            var catalog = await admin.GetFromJsonAsync<PagedResult<ProviderCatalogEntry>>("/api/provider-catalog");
            Assert.That(catalog!.Items.Select(e => e.ProviderType), Does.Not.Contain("blender-4"));
        });
    }

    [Test]
    public async Task ContentEndpoint_ServesTheFragment_OnlyWithAValidResolutionToken()
    {
        await CreateClient().PostAsJsonAsync("/api/environment-layers", Blender());
        var runId = Guid.NewGuid();
        await Factory.Services.GetRequiredService<IDataAccess<CoreRunResolutionRecord>>()
            .SaveAsync(new CoreRunResolutionRecord { Id = runId, ResolutionToken = "tok-1" });
        var anonymous = Factory.CreateClient();

        var ok = await anonymous.GetAsync(
            $"/api/environment-layers/blender-4/content?runId={runId}&token=tok-1");
        var wrongToken = await anonymous.GetAsync(
            $"/api/environment-layers/blender-4/content?runId={runId}&token=wrong");

        Assert.Multiple(async () =>
        {
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var fragment = await ok.Content.ReadAsStringAsync();
            Assert.That(fragment, Does.StartWith("RUN "),
                "the Core generates the build fragment from the setup script");
            Assert.That(fragment, Does.Contain("base64 -d"),
                "the script travels base64 — no Dockerfile escaping pitfalls");
            Assert.That(wrongToken.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }

    [Test]
    public async Task ContentEndpoint_WindowsBase_IsNotServedToLinuxComposition()
    {
        await CreateClient().PostAsJsonAsync("/api/environment-layers", new UpsertEnvironmentLayer(
            "msbuild-17", "Visual Studio build tools", "choco install visualstudio2022buildtools",
            BaseEnvironment: EnvironmentBases.Windows));
        var runId = Guid.NewGuid();
        await Factory.Services.GetRequiredService<IDataAccess<CoreRunResolutionRecord>>()
            .SaveAsync(new CoreRunResolutionRecord { Id = runId, ResolutionToken = "tok-2" });

        var response = await Factory.CreateClient().GetAsync(
            $"/api/environment-layers/msbuild-17/content?runId={runId}&token=tok-2");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "a linux composition must never receive a windows-only layer");
    }

    [Test]
    public async Task ContentEndpoint_ServesTheVariantMatchingTheRunnersBase()
    {
        var admin = CreateClient();
        await admin.PostAsJsonAsync("/api/environment-layers", Blender());
        await admin.PostAsJsonAsync("/api/environment-layers", new UpsertEnvironmentLayer(
            "blender-4", "Blender 4 preinstalled", "choco install -y blender",
            BaseEnvironment: EnvironmentBases.Windows, Version: "4.2"));
        var runId = Guid.NewGuid();
        await Factory.Services.GetRequiredService<IDataAccess<CoreRunResolutionRecord>>()
            .SaveAsync(new CoreRunResolutionRecord { Id = runId, ResolutionToken = "tok-3" });
        var anonymous = Factory.CreateClient();

        var linux = await anonymous.GetAsync(
            $"/api/environment-layers/blender-4/content?runId={runId}&token=tok-3&base=linux");
        var windows = await anonymous.GetAsync(
            $"/api/environment-layers/blender-4/content?runId={runId}&token=tok-3&base=windows");

        Assert.Multiple(async () =>
        {
            Assert.That(linux.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await linux.Content.ReadAsStringAsync(), Does.Contain("base64 -d"));
            Assert.That(windows.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await windows.Content.ReadAsStringAsync(), Does.Contain("powershell"),
                "the windows fragment executes the script via PowerShell with stop-on-error semantics");
        });
    }

    [Test]
    public async Task DeleteVariant_KeepsSiblings_AndTheLastRemovesTheLayer()
    {
        var admin = CreateClient();
        await admin.PostAsJsonAsync("/api/environment-layers", Blender());
        await admin.PostAsJsonAsync("/api/environment-layers", new UpsertEnvironmentLayer(
            "blender-4", "Blender 4 preinstalled", "choco install -y blender",
            BaseEnvironment: EnvironmentBases.Windows, Version: "4.2"));

        var afterFirst = await admin.DeleteAsync("/api/environment-layers/blender-4/variants/windows");
        Assert.That(afterFirst.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var layer = await admin.GetFromJsonAsync<EnvironmentLayerDto>("/api/environment-layers/blender-4");
        Assert.That(layer!.Variants.Single().BaseEnvironment, Is.EqualTo("linux"));

        var afterLast = await admin.DeleteAsync("/api/environment-layers/blender-4/variants/linux");
        Assert.Multiple(async () =>
        {
            Assert.That(afterLast.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                (await admin.GetAsync("/api/environment-layers/blender-4")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound),
                "a layer without a single variant composes nowhere and disappears");
            var catalog = await admin.GetFromJsonAsync<PagedResult<ProviderCatalogEntry>>("/api/provider-catalog");
            Assert.That(catalog!.Items.Select(e => e.ProviderType), Does.Not.Contain("blender-4"));
        });
    }

    [Test]
    public async Task Writes_WithoutTheCatalogPermission_AreForbidden()
    {
        var user = await ClientWithRolesAsync("User");

        var response = await user.PostAsJsonAsync("/api/environment-layers", Blender());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    private async Task<HttpClient> ClientWithRolesAsync(params string[] roles)
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}");
        foreach (var role in roles)
            await directory.AssignRoleAsync(principal.Id, role);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}

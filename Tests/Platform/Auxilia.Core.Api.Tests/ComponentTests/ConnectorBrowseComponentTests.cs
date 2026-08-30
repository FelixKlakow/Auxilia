using System.Net;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The connector-browse route + DTO wire-up through the typed <see cref="ICoreClient"/> — the
/// repository/branch pickers in the operator UIs ride exclusively on
/// <see cref="ICoreClient.BrowseConnectorAsync"/>, so this is the tier where a wiring regression
/// would otherwise only surface as a broken picker at runtime. Browsing executes the browse
/// specs REGISTERED with the provider (data-driven; no vendor code in the Core), so the test
/// registers the tfs-account payload first. The upstream Git REST API is a stubbed HTTP
/// handler; the browse LOGIC is covered by ConnectorBrowseAzureDevOpsTests and
/// ConnectorBrowseSpecTests.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ConnectorBrowseComponentTests : CoreApiComponentTestBase
{
    private const string OrgUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string CloneUrl = OrgUrl + "/Tools/_git/Bridge";

    private static readonly string RepositoriesJson = JsonSerializer.Serialize(new
    {
        count = 1,
        value = new object[]
        {
            new
            {
                id = "repo-1",
                name = "Bridge",
                remoteUrl = CloneUrl,
                project = new { id = "proj-1", name = "Tools" },
            },
        },
    });

    private static readonly string RefsJson = JsonSerializer.Serialize(new
    {
        count = 2,
        value = new object[] { new { name = "refs/heads/main" }, new { name = "refs/heads/wip" } },
    });

    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(
                new StubHttpMessageHandler(request =>
                {
                    var url = request.RequestUri!.AbsoluteUri;
                    if (url.Contains("/refs?", StringComparison.Ordinal))
                        return Json(RefsJson);
                    if (url.Contains("/_apis/git/repositories?", StringComparison.Ordinal))
                        return Json(RepositoriesJson);
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                })));
        });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static Task RegisterTfsAccountAsync(ICoreClient core)
        => core.RegisterProviderAsync(new RegisterSlotProvider(
            "tfs-account", "account", null, ["git-credential"], [],
            BrowseSpecs: UnitTests.ConnectorBrowseAzureDevOpsTests.TfsBrowseSpecs));

    [Test]
    public async Task BrowseRepositoriesThenBranches_RoundTripsThroughTheClient()
    {
        ICoreClient core = new CoreClient(CreateClient());
        await RegisterTfsAccountAsync(core);
        var connector = await core.CreateConnectorAsync(new CreateConnector(
            "tfs", "tfs-account", new Dictionary<string, string>
            {
                ["token"] = "ado-pat",
                ["OrgUrl"] = OrgUrl,
            }));

        var repositories = await core.BrowseConnectorAsync(
            connector.Id, new BrowseConnector("repositories"));
        Assert.That(repositories.Items.Select(i => (i.Id, i.Label)),
            Is.EqualTo(new[] { (CloneUrl, "Tools/Bridge") }));

        // Context carries the sibling setting (the clone URL) — the branch-picker path.
        var branches = await core.BrowseConnectorAsync(
            connector.Id, new BrowseConnector("branches", Context: CloneUrl));
        Assert.That(branches.Items.Select(i => i.Id), Is.EqualTo(new[] { "main", "wip" }));
    }

    [Test]
    public async Task BrowseWithoutACredential_SurfacesA400_SoPickersFallBackToManualEntry()
    {
        ICoreClient core = new CoreClient(CreateClient());
        await RegisterTfsAccountAsync(core);
        var bare = await core.CreateConnectorAsync(new CreateConnector(
            "bare", "tfs-account", new Dictionary<string, string> { ["OrgUrl"] = OrgUrl }));

        var ex = Assert.ThrowsAsync<CoreApiException>(
            () => core.BrowseConnectorAsync(bare.Id, new BrowseConnector("repositories")));
        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public void BrowseAnUnknownConnector_SurfacesA404()
    {
        ICoreClient core = new CoreClient(CreateClient());

        var ex = Assert.ThrowsAsync<CoreApiException>(
            () => core.BrowseConnectorAsync(Guid.NewGuid(), new BrowseConnector("repositories")));
        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}

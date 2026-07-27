using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Azure DevOps / TFS browsing against a MOCKED HTTP layer speaking the real Git REST API
/// response shapes (no official ADO container exists — the server is Windows-only and
/// licensed). The stub asserts the exact URLs and the PAT basic-auth header the service sends,
/// so a real-tenant verification only has to confirm connectivity, not behavior.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class ConnectorBrowseAzureDevOpsTests
{
    private const string OrgUrl = "https://tfs.example.com/tfs/DefaultCollection";
    private const string Pat = "ado-pat-secret";

    private static readonly string RepositoriesJson = JsonSerializer.Serialize(new
    {
        count = 2,
        value = new object[]
        {
            new
            {
                id = "repo-guid-1",
                name = "Praetorium.Bridge",
                remoteUrl = "https://tfs.example.com/tfs/DefaultCollection/Tools/_git/Praetorium.Bridge",
                project = new { id = "proj-guid-1", name = "Tools" },
            },
            new
            {
                id = "repo-guid-2",
                name = "Alpha",
                remoteUrl = "https://tfs.example.com/tfs/DefaultCollection/Alpha/_git/Alpha",
                project = new { id = "proj-guid-2", name = "Alpha" },
            },
        },
    });

    private static readonly string RefsJson = JsonSerializer.Serialize(new
    {
        count = 2,
        value = new object[]
        {
            new { name = "refs/heads/main" },
            new { name = "refs/heads/feature/steering" },
        },
    });

    private readonly List<HttpRequestMessage> _requests = [];
    private ConnectorBrowseService _sut = null!;
    private Guid _connectorId;

    [SetUp]
    public async Task SetUp()
    {
        _requests.Clear();
        var handler = new StubHttpMessageHandler(request =>
        {
            _requests.Add(request);
            var path = request.RequestUri!.AbsoluteUri;
            if (path.Contains("/refs?", StringComparison.Ordinal))
                return Json(RefsJson);
            if (path.Contains("/_apis/git/repositories?", StringComparison.Ordinal))
                return Json(RepositoriesJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        var connectors = new ConnectorService(
            new InMemoryDataAccess<Auxilia.Core.Api.Data.CoreConnectorRecord>(), protector, TimeProvider.System);
        var refresher = new ConnectorTokenRefresher(
            connectors,
            new ProviderCatalogService(
                new InMemoryDataAccess<Auxilia.PlatformData.Entities.SlotProviderRecord>(),
                new InMemoryDataAccess<Auxilia.PlatformData.Entities.ProviderCatalogRecord>(),
                new AuditLog(new InMemoryDataAccess<Auxilia.PlatformData.Entities.AuditRecord>(), TimeProvider.System)),
            new StubHttpClientFactory(handler), TimeProvider.System,
            NullLogger<ConnectorTokenRefresher>.Instance);
        _sut = new ConnectorBrowseService(
            refresher, new StubHttpClientFactory(handler), NullLogger<ConnectorBrowseService>.Instance);

        _connectorId = (await connectors.CreateAsync(
            new CreateConnector("tfs", "tfs-account", new Dictionary<string, string>
            {
                ["token"] = Pat,
                ["OrgUrl"] = OrgUrl + "/", // trailing slash must be tolerated
            }), null, CancellationToken.None)).Id;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Test]
    public async Task Repositories_ListsAllRepos_LabeledProjectSlashName_WithPatBasicAuth()
    {
        var result = await _sut.BrowseAsync(
            _connectorId, new BrowseConnector("repositories"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Items.Select(i => (i.Id, i.Label)), Is.EqualTo(new[]
            {
                ("https://tfs.example.com/tfs/DefaultCollection/Alpha/_git/Alpha", "Alpha"),
                ("https://tfs.example.com/tfs/DefaultCollection/Tools/_git/Praetorium.Bridge",
                 "Tools/Praetorium.Bridge"),
            }), "clone URL as the machine value; Project/Name label, collapsed when they coincide");
            Assert.That(_requests.Single().RequestUri!.AbsoluteUri,
                Is.EqualTo($"{OrgUrl}/_apis/git/repositories?api-version=7.1"));
            var auth = _requests.Single().Headers.Authorization!;
            Assert.That(auth.Scheme, Is.EqualTo("Basic"));
            Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)),
                Is.EqualTo($":{Pat}"), "PAT basic auth uses an empty user name — cloud and on-prem alike");
        });
    }

    [Test]
    public async Task Branches_ResolvesTheRepoByCloneUrl_AndStripsRefsHeads()
    {
        var result = await _sut.BrowseAsync(
            _connectorId,
            new BrowseConnector("branches",
                "https://felix@tfs.example.com/tfs/DefaultCollection/Tools/_git/Praetorium.Bridge"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Items.Select(i => i.Id), Is.EqualTo(new[] { "main", "feature/steering" }));
            Assert.That(_requests[^1].RequestUri!.AbsoluteUri,
                Is.EqualTo($"{OrgUrl}/proj-guid-1/_apis/git/repositories/repo-guid-1/refs"
                           + "?filter=heads/&api-version=7.1"),
                "the clone URL (userinfo and all) must resolve to the repo's project + id");
        });
    }

    [Test]
    public async Task Branches_FallsBackToTheRepoName_WhenTheRemoteUrlDiffers()
    {
        var result = await _sut.BrowseAsync(
            _connectorId,
            new BrowseConnector("branches", "https://mirror.example.com/whatever/Alpha.git"),
            CancellationToken.None);

        Assert.That(_requests[^1].RequestUri!.AbsoluteUri, Does.Contain("repo-guid-2"),
            "an unmatched remote URL still resolves by the repository name");
        Assert.That(result.Items, Is.Not.Empty);
    }

    [Test]
    public void Branches_UnknownRepository_SurfacesAsNotSupported_SoClientsFallBackToManualEntry()
    {
        Assert.ThrowsAsync<NotSupportedException>(() => _sut.BrowseAsync(
            _connectorId,
            new BrowseConnector("branches", "https://tfs.example.com/tfs/Other/_git/DoesNotExist"),
            CancellationToken.None));
    }

    [TestCase("https://felix@tfs.example.com/tfs/DefaultCollection/Tools/_git/Repo.git",
        "tfs.example.com/tfs/defaultcollection/tools/_git/repo")]
    [TestCase("https://dev.azure.com/org/Proj/_git/My%20Repo",
        "dev.azure.com/org/proj/_git/my repo")]
    public void NormalizeRepoUrl_StripsSchemeUserinfoAndGitSuffix(string url, string expected)
    {
        Assert.That(ConnectorBrowseService.NormalizeRepoUrl(url), Is.EqualTo(expected));
    }
}

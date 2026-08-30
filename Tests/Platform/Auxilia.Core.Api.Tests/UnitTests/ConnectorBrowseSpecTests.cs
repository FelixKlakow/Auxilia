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

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// The generic, data-driven browse-spec executor: placeholder substitution, declared auth
/// styles, declared headers, and response mapping — plus the not-supported fallbacks that let
/// clients fall back to manual entry. The GitHub specs used here are the SAME DATA the
/// github-account registration payload carries (Scripts/Setup-Platform.ps1 and the core-data
/// seed) — the Core itself knows no vendor.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class ConnectorBrowseSpecTests
{
    private const string Token = "gh-token-secret";

    /// <summary>The github-account browse registration — the same DATA Setup-Platform.ps1 posts.</summary>
    internal static readonly Dictionary<string, ProviderBrowseSpec> GitHubBrowseSpecs = new()
    {
        ["repositories"] = new ProviderBrowseSpec(
            UrlTemplate: "https://api.github.com/user/repos?per_page=100&sort=pushed",
            IdField: "clone_url",
            LabelField: "full_name",
            Headers: new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json",
                ["User-Agent"] = "Auxilia-Core",
            },
            BearerSettingKey: "token"),
        ["branches"] = new ProviderBrowseSpec(
            UrlTemplate: "https://api.github.com/repos/{context:owner-repo}/branches?per_page=100",
            IdField: "name",
            LabelField: "name",
            Headers: new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json",
                ["User-Agent"] = "Auxilia-Core",
            },
            BearerSettingKey: "token"),
    };

    private static readonly string ReposJson = JsonSerializer.Serialize(new object[]
    {
        new { clone_url = "https://github.com/acme/widget.git", full_name = "acme/widget" },
        new { clone_url = "https://github.com/acme/gadget.git", full_name = "acme/gadget" },
        new { clone_url = "", full_name = "acme/broken" }, // no machine value -> filtered out
    });

    private static readonly string BranchesJson = JsonSerializer.Serialize(new object[]
    {
        new { name = "main" },
        new { name = "feature/x" },
    });

    private readonly List<HttpRequestMessage> _requests = [];
    private ConnectorBrowseService _sut = null!;
    private ProviderCatalogService _catalog = null!;
    private ConnectorService _connectors = null!;

    [SetUp]
    public void SetUp()
    {
        _requests.Clear();
        var handler = new StubHttpMessageHandler(request =>
        {
            _requests.Add(request);
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/branches?", StringComparison.Ordinal))
                return Json(BranchesJson);
            if (url.Contains("/user/repos?", StringComparison.Ordinal))
                return Json(ReposJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        _connectors = new ConnectorService(
            new InMemoryDataAccess<Auxilia.Core.Api.Data.CoreConnectorRecord>(), protector,
            new AccessGrantEvaluator(
                new InMemoryDataAccess<Auxilia.PlatformData.Entities.PrincipalRecord>(),
                new InMemoryDataAccess<Auxilia.PlatformData.Entities.GroupMembershipRecord>()),
            TimeProvider.System);
        _catalog = new ProviderCatalogService(
            new InMemoryDataAccess<Auxilia.PlatformData.Entities.SlotProviderRecord>(),
            new InMemoryDataAccess<Auxilia.PlatformData.Entities.ProviderCatalogRecord>(),
            new AuditLog(new InMemoryDataAccess<Auxilia.PlatformData.Entities.AuditRecord>(), TimeProvider.System));
        var refresher = new ConnectorTokenRefresher(
            _connectors, _catalog,
            new StubHttpClientFactory(handler), TimeProvider.System,
            NullLogger<ConnectorTokenRefresher>.Instance);
        _sut = new ConnectorBrowseService(
            refresher, _connectors, _catalog,
            new StubHttpClientFactory(handler), NullLogger<ConnectorBrowseService>.Instance);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private async Task<Guid> RegisterAndCreateAsync(
        IReadOnlyDictionary<string, ProviderBrowseSpec>? specs,
        Dictionary<string, string>? settings = null)
    {
        await _catalog.RegisterAsync("test", new RegisterSlotProvider(
            "github-account", "account", null, ["git-credential"], [],
            BrowseSpecs: specs), CancellationToken.None);
        return (await _connectors.CreateAsync(
            new CreateConnector("gh", "github-account",
                settings ?? new Dictionary<string, string> { ["token"] = Token }),
            null, CancellationToken.None)).Id;
    }

    [Test]
    public async Task Repositories_SendsBearerAuthAndDeclaredHeaders_AndMapsDeclaredFields()
    {
        var connectorId = await RegisterAndCreateAsync(GitHubBrowseSpecs);

        var result = await _sut.BrowseAsync(
            connectorId, new BrowseConnector("repositories"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Items.Select(i => (i.Id, i.Label)), Is.EqualTo(new[]
            {
                ("https://github.com/acme/widget.git", "acme/widget"),
                ("https://github.com/acme/gadget.git", "acme/gadget"),
            }), "root-array response mapped by the declared id/label fields; empty ids filtered");
            var request = _requests.Single();
            Assert.That(request.RequestUri!.AbsoluteUri,
                Is.EqualTo("https://api.github.com/user/repos?per_page=100&sort=pushed"));
            Assert.That(request.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
            Assert.That(request.Headers.Authorization!.Parameter, Is.EqualTo(Token));
            Assert.That(request.Headers.Accept.ToString(), Is.EqualTo("application/vnd.github+json"));
            Assert.That(request.Headers.UserAgent.ToString(), Is.EqualTo("Auxilia-Core"));
        });
    }

    [Test]
    public async Task Branches_DerivesOwnerRepoFromTheContextCloneUrl()
    {
        var connectorId = await RegisterAndCreateAsync(GitHubBrowseSpecs);

        var result = await _sut.BrowseAsync(
            connectorId,
            new BrowseConnector("branches", "https://github.com/acme/widget.git"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Items.Select(i => i.Id), Is.EqualTo(new[] { "main", "feature/x" }));
            Assert.That(_requests.Single().RequestUri!.AbsoluteUri,
                Is.EqualTo("https://api.github.com/repos/acme/widget/branches?per_page=100"),
                "{context:owner-repo} strips the host and the .git suffix");
        });
    }

    [Test]
    public async Task UnknownKind_SurfacesAsNotSupported_SoClientsFallBackToManualEntry()
    {
        var connectorId = await RegisterAndCreateAsync(GitHubBrowseSpecs);

        var ex = Assert.ThrowsAsync<NotSupportedException>(() => _sut.BrowseAsync(
            connectorId, new BrowseConnector("tags"), CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("declares no 'tags' browse"));
    }

    [Test]
    public async Task ProviderWithoutBrowseSpecs_SurfacesAsNotSupported()
    {
        var connectorId = await RegisterAndCreateAsync(specs: null);

        Assert.ThrowsAsync<NotSupportedException>(() => _sut.BrowseAsync(
            connectorId, new BrowseConnector("repositories"), CancellationToken.None));
    }

    [Test]
    public async Task UnregisteredProvider_SurfacesAsNotSupported()
    {
        var connectorId = (await _connectors.CreateAsync(
            new CreateConnector("gh", "never-registered",
                new Dictionary<string, string> { ["token"] = Token }),
            null, CancellationToken.None)).Id;

        Assert.ThrowsAsync<NotSupportedException>(() => _sut.BrowseAsync(
            connectorId, new BrowseConnector("repositories"), CancellationToken.None));
    }

    [Test]
    public async Task MissingCredentialSetting_SurfacesAsNotSupported()
    {
        var connectorId = await RegisterAndCreateAsync(GitHubBrowseSpecs,
            settings: new Dictionary<string, string> { ["note"] = "no token here" });

        var ex = Assert.ThrowsAsync<NotSupportedException>(() => _sut.BrowseAsync(
            connectorId, new BrowseConnector("repositories"), CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("credential"));
    }

    [Test]
    public async Task ContextPlaceholderWithoutAContext_SurfacesAsNotSupported()
    {
        var connectorId = await RegisterAndCreateAsync(GitHubBrowseSpecs);

        Assert.ThrowsAsync<NotSupportedException>(() => _sut.BrowseAsync(
            connectorId, new BrowseConnector("branches"), CancellationToken.None));
    }

    [Test]
    public void Substitute_FillsSettingsContextAndResolvedValues_AndTrimsSettingSlashes()
    {
        var settings = new Dictionary<string, string> { ["OrgUrl"] = "https://ado.example.com/Org/" };
        var resolved = new Dictionary<string, string> { ["resolved.id"] = "repo-1" };

        var url = ConnectorBrowseService.Substitute(
            "{OrgUrl}/{resolved.id}/refs?of={context:owner-repo}",
            settings, "https://host/acme/widget.git", resolved);

        Assert.That(url, Is.EqualTo("https://ado.example.com/Org/repo-1/refs?of=acme/widget"));
    }

    [Test]
    public void Substitute_MissingSetting_SurfacesAsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => ConnectorBrowseService.Substitute(
            "{OrgUrl}/x", new Dictionary<string, string>(), null,
            new Dictionary<string, string>()));
    }
}

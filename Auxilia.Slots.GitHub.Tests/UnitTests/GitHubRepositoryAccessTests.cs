using System.Net;
using System.Text;
using Auxilia.Slots.GitHub;
using Auxilia.Workflows.SourceControl;

namespace Auxilia.Slots.GitHub.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class GitHubRepositoryAccessTests
{
    private static GitHubRepositoryOptions Options(string? token = null, string branch = "main") => new()
    {
        Owner = "acme",
        Repository = "widget",
        Token = token,
        Branch = branch
    };

    [Test]
    public async Task ListFilesAsync_MapsTreeBlobs()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "tree": [
                { "path": "src", "type": "tree" },
                { "path": "src/Program.cs", "type": "blob" },
                { "path": "README.md", "type": "blob" }
              ]
            }
            """);
        using var access = new GitHubRepositoryAccess(Options(), handler);

        var files = await access.ListFilesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(files, Is.EqualTo(new[] { "src/Program.cs", "README.md" }));
            Assert.That(handler.LastRequest!.RequestUri!.PathAndQuery,
                Is.EqualTo("/repos/acme/widget/git/trees/main?recursive=1"));
        });
    }

    [Test]
    public async Task ListFilesAsync_FiltersByRelativePathPrefix()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "tree": [
                { "path": "src/Program.cs", "type": "blob" },
                { "path": "src/Util/Helper.cs", "type": "blob" },
                { "path": "srcfoo/Other.cs", "type": "blob" },
                { "path": "README.md", "type": "blob" }
              ]
            }
            """);
        using var access = new GitHubRepositoryAccess(Options(), handler);

        var files = await access.ListFilesAsync("src");

        Assert.That(files, Is.EqualTo(new[] { "src/Program.cs", "src/Util/Helper.cs" }));
    }

    [Test]
    public async Task ReadFileContentAsync_ReturnsRawBody()
    {
        var handler = new StubHttpMessageHandler("file body");
        using var access = new GitHubRepositoryAccess(Options(branch: "develop"), handler);

        var content = await access.ReadFileContentAsync("src/Program.cs");

        Assert.Multiple(() =>
        {
            Assert.That(content, Is.EqualTo("file body"));
            Assert.That(handler.LastRequest!.RequestUri!.PathAndQuery,
                Is.EqualTo("/repos/acme/widget/contents/src/Program.cs?ref=develop"));
            Assert.That(handler.LastRequest.Headers.Accept.ToString(),
                Does.Contain("application/vnd.github.raw+json"));
        });
    }

    [Test]
    public async Task GetChangedFilesAsync_MapsStatusesToChangeKinds()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "files": [
                { "filename": "a.cs", "status": "added" },
                { "filename": "b.cs", "status": "removed" },
                { "filename": "c.cs", "status": "modified" },
                { "filename": "d.cs", "status": "renamed" }
              ]
            }
            """);
        using var access = new GitHubRepositoryAccess(Options(), handler);

        var changes = await access.GetChangedFilesAsync("main", "feature");

        Assert.Multiple(() =>
        {
            Assert.That(changes, Is.EqualTo(new[]
            {
                new ChangedFile("a.cs", ChangeKind.Added),
                new ChangedFile("b.cs", ChangeKind.Deleted),
                new ChangedFile("c.cs", ChangeKind.Modified),
                new ChangedFile("d.cs", ChangeKind.Renamed)
            }));
            Assert.That(handler.LastRequest!.RequestUri!.PathAndQuery,
                Is.EqualTo("/repos/acme/widget/compare/main...feature"));
        });
    }

    [Test]
    public async Task Token_SendsBearerAuthorizationHeader()
    {
        var handler = new StubHttpMessageHandler("""{ "tree": [] }""");
        using var access = new GitHubRepositoryAccess(Options(token: "github_pat_test"), handler);

        await access.ListFilesAsync();

        var authorization = handler.LastRequest!.Headers.Authorization;
        Assert.Multiple(() =>
        {
            Assert.That(authorization?.Scheme, Is.EqualTo("Bearer"));
            Assert.That(authorization?.Parameter, Is.EqualTo("github_pat_test"));
        });
    }

    [Test]
    public async Task NoToken_SendsNoAuthorizationHeader()
    {
        var handler = new StubHttpMessageHandler("""{ "tree": [] }""");
        using var access = new GitHubRepositoryAccess(Options(), handler);

        await access.ListFilesAsync();

        Assert.That(handler.LastRequest!.Headers.Authorization, Is.Null);
    }

    [Test]
    public async Task Requests_CarryUserAgentHeader()
    {
        var handler = new StubHttpMessageHandler("""{ "tree": [] }""");
        using var access = new GitHubRepositoryAccess(Options(), handler);

        await access.ListFilesAsync();

        Assert.That(handler.LastRequest!.Headers.UserAgent.ToString(), Does.Contain("Auxilia"));
    }

    [Test]
    public void FailedRequest_ThrowsWithoutLeakingToken()
    {
        var handler = new StubHttpMessageHandler("not found", HttpStatusCode.NotFound);
        using var access = new GitHubRepositoryAccess(Options(token: "github_pat_secret"), handler);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => access.ListFilesAsync());

        Assert.That(exception!.Message, Does.Not.Contain("github_pat_secret"));
    }

    [Test]
    public void WorkingPath_IsEmptyForApiBackedRepository()
    {
        using var access = new GitHubRepositoryAccess(Options(), new StubHttpMessageHandler("{}"));

        Assert.That(access.WorkingPath, Is.Empty);
    }

    private sealed class StubHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8)
            });
        }
    }
}

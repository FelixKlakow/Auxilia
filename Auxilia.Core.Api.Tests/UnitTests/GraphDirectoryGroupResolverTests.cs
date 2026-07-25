using System.Net;
using System.Net.Http.Headers;
using Auxilia.Core.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Runs the real <see cref="GraphDirectoryGroupResolver"/> against a faked Microsoft Graph endpoint —
/// proving the overage lookup's request/response handling without a live tenant.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class GraphDirectoryGroupResolverTests
{
    private static GraphDirectoryGroupResolver New(StubHttpMessageHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<GraphDirectoryGroupResolver>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Test]
    public async Task GetGroupIds_PostsGetMemberGroups_WithTheBearerToken_AndReturnsTheIds()
    {
        using var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, """{"value":["group-a","group-b"]}"""));

        var groups = await New(handler).GetGroupIdsAsync("oid-1", "user-token", CancellationToken.None);

        Assert.That(groups, Is.EquivalentTo(new[] { "group-a", "group-b" }));
        Assert.Multiple(() =>
        {
            Assert.That(handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("https://graph.microsoft.com/v1.0/me/getMemberGroups"));
            Assert.That(handler.LastRequest.Headers.Authorization,
                Is.EqualTo(new AuthenticationHeaderValue("Bearer", "user-token")));
            Assert.That(handler.LastBody, Does.Contain("securityEnabledOnly"));
        });
    }

    [Test]
    public async Task GetGroupIds_ReturnsEmpty_WithoutAnAccessToken_AndMakesNoCall()
    {
        using var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """{"value":["x"]}"""));

        var groups = await New(handler).GetGroupIdsAsync("oid-1", accessToken: null, CancellationToken.None);

        Assert.That(groups, Is.Empty);
        Assert.That(handler.LastRequest, Is.Null, "No Graph call is made without a token.");
    }

    [Test]
    public async Task GetGroupIds_ReturnsEmpty_WhenGraphRefuses()
    {
        using var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied"}}"""));

        Assert.That(await New(handler).GetGroupIdsAsync("oid-1", "user-token", CancellationToken.None), Is.Empty,
            "A Graph failure degrades to no directory roles (deny-by-default), never a throw.");
    }
}

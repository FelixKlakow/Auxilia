using System.Net;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Runs the real <see cref="EntraOboTokenExchange"/> against a faked token endpoint — proving the
/// actual on-behalf-of request and response handling without a live tenant.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EntraOboTokenExchangeTests
{
    private static EntraOboTokenExchange New(StubHttpMessageHandler handler)
        => new(new StubHttpClientFactory(handler),
            Options.Create(new OidcSettings
            {
                Authority = "https://login.microsoftonline.com/contoso/v2.0",
                ClientId = "client-abc",
                ClientSecret = "shhh"
            }),
            NullLogger<EntraOboTokenExchange>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Test]
    public async Task Exchange_PostsTheObiGrant_ToTheDerivedTokenEndpoint_AndReturnsTheToken()
    {
        using var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"downstream-token","expires_in":3599}"""));

        var token = await New(handler).ExchangeAsync("user-token", "api://ado", CancellationToken.None);

        Assert.That(token, Is.EqualTo("downstream-token"));
        Assert.Multiple(() =>
        {
            Assert.That(handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("https://login.microsoftonline.com/contoso/oauth2/v2.0/token"),
                "The v2.0 token endpoint is derived from the authority.");
            Assert.That(handler.LastBody, Does.Contain("requested_token_use=on_behalf_of"));
            Assert.That(handler.LastBody, Does.Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer"));
            Assert.That(handler.LastBody, Does.Contain("assertion=user-token"));
            Assert.That(handler.LastBody, Does.Contain("scope=api%3A%2F%2Fado%2F.default"),
                "A bare resource is suffixed with /.default.");
        });
    }

    [Test]
    public async Task Exchange_PreservesAnExplicitDefaultScope()
    {
        using var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"t"}"""));

        await New(handler).ExchangeAsync("user-token", "499b/.default", CancellationToken.None);

        Assert.That(handler.LastBody, Does.Contain("scope=499b%2F.default"));
    }

    [Test]
    public async Task Exchange_ReturnsNull_WhenEntraRefuses()
    {
        using var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        Assert.That(await New(handler).ExchangeAsync("user-token", "api://ado", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Exchange_ReturnsNull_WhenTheEntraAppIsNotConfigured()
    {
        using var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var exchange = new EntraOboTokenExchange(
            new StubHttpClientFactory(handler),
            Options.Create(new OidcSettings()), // no authority/client id/secret
            NullLogger<EntraOboTokenExchange>.Instance);

        Assert.That(await exchange.ExchangeAsync("user-token", "api://ado", CancellationToken.None), Is.Null);
        Assert.That(handler.LastRequest, Is.Null, "No request is made when the relying party is unconfigured.");
    }
}

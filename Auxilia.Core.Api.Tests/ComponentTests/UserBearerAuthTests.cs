using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// End-to-end tests for the per-user bearer (Phase 3a-ii): a cookie-authenticated user mints a token
/// via <c>POST /auth/token</c>; that token authorizes subsequent Core requests AS the same principal;
/// and the security boundaries hold — expired / tampered / disabled tokens fail, API-key auth is
/// unaffected, and only a cookie session (never an API key) may mint a token. The interactive sign-in
/// is stubbed exactly as <see cref="OidcLoginTests"/> does (no live tenant).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class UserBearerAuthTests : CoreApiComponentTestBase
{
    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.ConfigureTestServices(services =>
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, StubExternalAuthHandler>(AuthSchemes.ExternalCookie, null));

    /// <summary>Signs a user in via the stubbed external provider; the returned client holds the session cookie.</summary>
    private async Task<(HttpClient Client, Guid PrincipalId)> SignInAsync(string subject = "sub-1")
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/callback?returnUrl=/auth/me");
        request.Headers.Add("X-Test-Sub", subject);
        request.Headers.Add("X-Test-Name", "Ada Lovelace");
        var me = await (await client.SendAsync(request)).Content.ReadFromJsonAsync<CurrentPrincipal>();
        return (client, me!.PrincipalId);
    }

    private static async Task<UserBearerToken> MintTokenAsync(HttpClient cookieClient)
    {
        var response = await cookieClient.PostAsync("/auth/token", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<UserBearerToken>())!;
    }

    private HttpClient BearerClient(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Test]
    public async Task MintedToken_AuthorizesAsTheSignedInPrincipal()
    {
        var (cookieClient, principalId) = await SignInAsync();
        var token = await MintTokenAsync(cookieClient);

        Assert.That(token.ExpiresUtc, Is.GreaterThan(DateTimeOffset.UtcNow));

        var me = await BearerClient(token.Token).GetFromJsonAsync<CurrentPrincipal>("/auth/me");
        Assert.That(me!.PrincipalId, Is.EqualTo(principalId),
            "The per-user bearer must authorize as the exact principal that minted it.");
    }

    [Test]
    public async Task ExpiredToken_Returns401()
    {
        var tokens = Factory.Services.GetRequiredService<UserBearerTokenService>();
        var expired = tokens.Protect(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await BearerClient(expired).GetAsync("/auth/me");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task TamperedToken_Returns401()
    {
        var tokens = Factory.Services.GetRequiredService<UserBearerTokenService>();
        var (token, _) = tokens.Issue(Guid.NewGuid());
        var i = token.Length - 2;
        var tampered = token[..i] + (token[i] == 'A' ? 'B' : 'A') + token[(i + 1)..];

        var response = await BearerClient(tampered).GetAsync("/auth/me");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task DisabledPrincipalToken_Returns401()
    {
        var (cookieClient, principalId) = await SignInAsync();
        var token = await MintTokenAsync(cookieClient);

        // Disable the principal AFTER the token was minted — its outstanding token must stop working.
        await Factory.Services.GetRequiredService<PrincipalDirectory>().SetEnabledAsync(principalId, false);

        var response = await BearerClient(token.Token).GetAsync("/auth/me");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ApiKeyAuth_StillWorks_AlongsideUserBearer()
    {
        // Regression: the aux_ API-key path (a distinct scheme + token shape) is unaffected.
        var response = await CreateClient().GetAsync("/api/runs");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task TokenEndpoint_RejectsApiKeyCaller()
    {
        // An API-key (service) principal must NOT be able to self-issue a user-scoped bearer.
        var response = await CreateClient().PostAsync("/auth/token", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task TokenEndpoint_RejectsAnonymousCaller()
    {
        var response = await CreateAnonymousClient().PostAsync("/auth/token", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task CoreClient_WithCallerTokenProvider_ActsAsTheUser_AndFallsBackToApiKey()
    {
        var (cookieClient, principalId) = await SignInAsync();
        var token = await MintTokenAsync(cookieClient);

        // A per-request provider: yields the user token → the client acts AS the user.
        var provider = new StubCallerTokenProvider(token.Token);
        var handler = new CoreCallerTokenHandler(provider, fallbackApiKey: TestApiKey)
        {
            InnerHandler = Factory.Server.CreateHandler()
        };
        var http = new HttpClient(handler) { BaseAddress = Factory.Server.BaseAddress };
        ICoreClient core = new CoreClient(http);

        var asUser = await core.GetCurrentPrincipalAsync();
        Assert.That(asUser.PrincipalId, Is.EqualTo(principalId));

        // With no user token, it falls back to the static API key (the bootstrap Administrator).
        provider.Token = null;
        var asService = await core.GetCurrentPrincipalAsync();
        Assert.That(asService.Roles, Does.Contain("Administrator"),
            "With no per-user token the client falls back to the app API key (unchanged service path).");
    }

    private sealed class StubCallerTokenProvider(string? token) : ICoreCallerTokenProvider
    {
        public string? Token { get; set; } = token;
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult(Token);
    }
}

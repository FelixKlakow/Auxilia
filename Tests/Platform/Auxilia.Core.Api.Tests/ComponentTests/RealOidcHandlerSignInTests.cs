using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Services;
using Auxilia.Governance;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Drives the <em>real</em> ASP.NET <c>AddOpenIdConnect</c> handler end to end against an in-test
/// mock IdP: the handler exchanges the code at a mocked token endpoint, validates a genuinely
/// RSA-signed ID token (issuer, audience, nonce, signature), and the flow provisions the principal,
/// maps its directory-group claim to a role, and — with delegation on — retains its token for OBO.
/// The stub-scheme tests bypass this handler; this one exercises it for real without a live tenant.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RealOidcHandlerSignInTests : CoreApiComponentTestBase
{
    private const string Issuer = "https://mock-idp.test";
    private const string ClientId = "test-client";

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "test-key" };

    [OneTimeTearDown]
    public void DisposeSigningKey() => Rsa.Dispose();

    private sealed record MeResponse(Guid PrincipalId, string? DisplayName, string[] Roles);

    // Mutated by the test after it captures the challenge's nonce; read when the mock mints the token.
    private sealed class TokenState { public string Nonce = ""; public string[] Groups = []; }
    private readonly TokenState _tokenState = new();

    /// <summary>Backchannel mock: the handler's only server call is the code→token exchange.</summary>
    private sealed class MockTokenHandler(Func<string> idToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"token_type":"Bearer","access_token":"user-access-token","expires_in":3600,"id_token":"{{idToken()}}"}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
    }

    private static string CreateIdToken(string nonce, IEnumerable<string> groups)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "entra-user-1",
                ["oid"] = "entra-user-1",
                ["name"] = "Ada Lovelace",
                ["nonce"] = nonce,
                ["groups"] = groups.ToArray()
            }
        });

    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Oidc:Enabled", "true");
        builder.UseSetting("Oidc:Authority", Issuer);
        builder.UseSetting("Oidc:ClientId", ClientId);
        builder.UseSetting("Oidc:ClientSecret", "secret");
        builder.UseSetting("Oidc:EnableDelegation", "true");
        builder.ConfigureTestServices(services =>
            services.PostConfigure<OpenIdConnectOptions>(AuthSchemes.Oidc, options =>
            {
                // Static config manager → the handler never fetches discovery (this must overwrite the
                // fetching manager the framework's own post-configure already built from the authority).
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer,
                    AuthorizationEndpoint = $"{Issuer}/authorize",
                    TokenEndpoint = $"{Issuer}/token"
                };
                configuration.SigningKeys.Add(SigningKey);
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                // Set the Backchannel client itself (not just the handler): the framework's own
                // post-configure already built one, so replacing the handler alone is too late.
                options.Backchannel = new HttpClient(
                    new MockTokenHandler(() => CreateIdToken(_tokenState.Nonce, _tokenState.Groups)));
                options.RequireHttpsMetadata = false;
                options.GetClaimsFromUserInfoEndpoint = false; // no userinfo endpoint on the mock
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.ValidAudience = ClientId;
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            }));
    }

    [Test]
    public async Task RealHandler_ValidatesSignedToken_Provisions_MapsGroupRole_AndRetainsTokenForObo()
    {
        await Factory.Services.GetRequiredService<GroupMappingDirectory>()
            .CreateAsync("entra", "group-ops", "Operator");
        _tokenState.Groups = ["group-ops"];

        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });

        // 1. Challenge → the real handler redirects to the authorize endpoint; capture state + nonce.
        var login = await client.GetAsync("/auth/login?returnUrl=/auth/me");
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var authorize = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        _tokenState.Nonce = authorize["nonce"]!;

        // 2. Callback with a code → the handler exchanges it (mock), validates the signed ID token,
        //    and signs the external identity in → redirect to /auth/callback.
        var callback = await client.GetAsync(
            $"/auth/oidc-callback?code=fake-code&state={Uri.EscapeDataString(authorize["state"]!)}");
        Assert.That(callback.StatusCode, Is.EqualTo(HttpStatusCode.Redirect),
            $"OIDC callback did not complete: {callback.StatusCode}");

        // 3. /auth/callback provisions + issues the session cookie → LocalRedirect to /auth/me.
        var provisioned = await client.GetAsync(callback.Headers.Location!.ToString());
        Assert.That(provisioned.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        // 4. The session cookie authenticates /auth/me.
        var me = await client.GetAsync(provisioned.Headers.Location!.ToString());
        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await me.Content.ReadFromJsonAsync<MeResponse>();

        Assert.Multiple(() =>
        {
            Assert.That(body!.DisplayName, Is.EqualTo("Ada Lovelace"), "Provisioned from the signed ID token.");
            Assert.That(body.Roles, Does.Contain("Operator"), "The token's group claim mapped to a role.");
        });

        var retained = await Factory.Services.GetRequiredService<DelegatedTokenStore>()
            .GetAsync(body!.PrincipalId, CancellationToken.None);
        Assert.That(retained, Is.EqualTo("user-access-token"),
            "Delegation retained the user's token from the real token exchange.");
    }
}

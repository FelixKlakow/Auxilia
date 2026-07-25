using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Exercises the interactive sign-in path (JIT provisioning + browser session issuance) end to end
/// via a stubbed external provider — no live Entra tenant. The stub occupies the external scheme the
/// real OIDC handler would; everything downstream of it (<c>/auth/callback</c> → provisioning → cookie
/// → policy) runs for real. API-key auth continues to work alongside (see <see cref="AuthTests"/>).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class OidcLoginTests : CoreApiComponentTestBase
{
    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.ConfigureTestServices(services =>
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, StubExternalAuthHandler>(AuthSchemes.ExternalCookie, null));

    private sealed record MeResponse(Guid PrincipalId, string? DisplayName, string[] Roles);

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string subject, string name = "Ada Lovelace")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/callback?returnUrl=/auth/me");
        request.Headers.Add("X-Test-Sub", subject);
        request.Headers.Add("X-Test-Name", name);
        return await client.SendAsync(request); // auto-redirect + cookie container → lands on /auth/me
    }

    [Test]
    public async Task SignIn_ProvisionsPrincipal_AndIssuesUsableSession()
    {
        var client = Factory.CreateClient();

        var response = await SignInAsync(client, "sub-1", "Ada Lovelace");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var me = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(me, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(me!.PrincipalId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(me.DisplayName, Is.EqualTo("Ada Lovelace"));
        });
    }

    [Test]
    public async Task SignIn_SameSubjectTwice_ResolvesTheSamePrincipal()
    {
        var first = await (await SignInAsync(Factory.CreateClient(), "sub-1")).Content.ReadFromJsonAsync<MeResponse>();
        var second = await (await SignInAsync(Factory.CreateClient(), "sub-1")).Content.ReadFromJsonAsync<MeResponse>();

        Assert.That(second!.PrincipalId, Is.EqualTo(first!.PrincipalId),
            "JIT provisioning must be idempotent per external subject.");
    }

    [Test]
    public async Task SignedInCookie_AuthenticatesNormalEndpoints_ButFreshUserIsDeniedAdminActions()
    {
        var client = Factory.CreateClient();
        await SignInAsync(client, "sub-1");

        // The session cookie authenticates an ordinary authorized endpoint…
        var runs = await client.GetAsync("/api/runs");
        // …but a freshly provisioned external user holds no roles, so deny-by-default applies.
        var admin = await client.PostAsJsonAsync("/api/groups", new { name = "should-be-denied" });

        Assert.Multiple(() =>
        {
            Assert.That(runs.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(admin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }

    [Test]
    public async Task Callback_WithoutAnExternalIdentity_Returns401()
    {
        var response = await Factory.CreateClient().GetAsync("/auth/callback");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ApiKeyAuth_StillWorks_AlongsideInteractiveSignIn()
    {
        var response = await CreateClient().GetAsync("/api/runs");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}

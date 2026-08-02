using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Api.Auth;
using Auxilia.Governance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        {
            // The stub occupies the external scheme the real OIDC handler would.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, StubExternalAuthHandler>(AuthSchemes.ExternalCookie, null);
            // Stand in for the Graph overage lookup: an over-quota user is "in" group-ops.
            services.RemoveAll<IDirectoryGroupResolver>();
            services.AddSingleton<IDirectoryGroupResolver>(new StubDirectoryGroupResolver(["group-ops"]));
        });

    private sealed record MeResponse(Guid PrincipalId, string? DisplayName, string[] Roles);

    /// <summary>Seeds a directory group → role mapping the sign-in flow resolves against.</summary>
    private Task SeedMappingAsync(string groupClaim, string roleName)
        => Factory.Services.GetRequiredService<GroupMappingDirectory>().CreateAsync("entra", groupClaim, roleName);

    private static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client, string subject, string name = "Ada Lovelace", string? groups = null, bool overage = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/callback?returnUrl=/auth/me");
        request.Headers.Add("X-Test-Sub", subject);
        request.Headers.Add("X-Test-Name", name);
        if (groups is not null)
            request.Headers.Add("X-Test-Groups", groups);
        if (overage)
            request.Headers.Add("X-Test-Overage", "true");
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

    [Test]
    public async Task SignIn_WithGroupClaims_GrantsMappedDirectoryRoles()
    {
        await SeedMappingAsync("group-ops", "Operator");

        var me = await (await SignInAsync(Factory.CreateClient(), "sub-1", groups: "group-ops"))
            .Content.ReadFromJsonAsync<MeResponse>();

        Assert.That(me!.Roles, Does.Contain("Operator"),
            "A directory group claim must grant its mapped role for the session.");
    }

    [Test]
    public async Task SignIn_WithGroupOverage_ReadsGroupsFromDirectory_AndGrantsMappedRoles()
    {
        // No groups ride in the token (overage); the directory resolver reports membership in group-ops.
        await SeedMappingAsync("group-ops", "Operator");

        var me = await (await SignInAsync(Factory.CreateClient(), "sub-1", overage: true))
            .Content.ReadFromJsonAsync<MeResponse>();

        Assert.That(me!.Roles, Does.Contain("Operator"),
            "Group-claim overage must fall back to the directory and still resolve mapped roles.");
    }
}

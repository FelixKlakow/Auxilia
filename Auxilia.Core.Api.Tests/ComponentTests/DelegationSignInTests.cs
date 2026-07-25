using System.Net.Http.Json;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// With delegation enabled, an interactive sign-in retains the user's access token (encrypted) so a
/// workflow can later act on-behalf-of them. The stub external scheme carries the token the real
/// OIDC handler would have saved.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class DelegationSignInTests : CoreApiComponentTestBase
{
    private sealed record MeResponse(Guid PrincipalId, string? DisplayName, string[] Roles);

    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Oidc:EnableDelegation", "true");
        builder.ConfigureTestServices(services =>
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, StubExternalAuthHandler>(AuthSchemes.ExternalCookie, null));
    }

    [Test]
    public async Task SignIn_WithDelegationEnabled_RetainsTheUsersTokenForObo()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/callback?returnUrl=/auth/me");
        request.Headers.Add("X-Test-Sub", "sub-1");
        request.Headers.Add("X-Test-AccessToken", "user-access-token");

        var me = await (await client.SendAsync(request)).Content.ReadFromJsonAsync<MeResponse>();

        var retained = await Factory.Services.GetRequiredService<DelegatedTokenStore>()
            .GetAsync(me!.PrincipalId, CancellationToken.None);
        Assert.That(retained, Is.EqualTo("user-access-token"),
            "Sign-in must retain the user's token so OBO delegation can act as them.");
    }
}

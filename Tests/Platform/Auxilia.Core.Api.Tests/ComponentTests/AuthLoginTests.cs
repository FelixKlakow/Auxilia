using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The non-browser sign-in path: POST /auth/login exchanges a human principal's username +
/// password for the per-user bearer; the token authenticates subsequent calls AS that user,
/// wrong credentials and disabled principals are rejected, and both outcomes are audited.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AuthLoginTests : CoreApiComponentTestBase
{
    private async Task<Guid> CreateHumanAsync(string username, string password)
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var principal = await directory.CreateHumanAsync("Jane Desktop", username, password);
        return principal.Id;
    }

    [Test]
    public async Task Login_WithValidPassword_MintsABearer_ThatAuthenticatesAsTheUser()
    {
        var principalId = await CreateHumanAsync("jane", "hunter2-jane");
        var anonymous = Factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/auth/login",
            new PasswordLoginRequest("jane", "hunter2-jane"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var token = await response.Content.ReadFromJsonAsync<UserBearerToken>();

        Assert.Multiple(() =>
        {
            Assert.That(token!.Token, Does.StartWith("auxu_"), "the SAME shape the browser path mints");
            Assert.That(token.ExpiresUtc, Is.GreaterThan(DateTimeOffset.UtcNow.AddHours(1)),
                "desktop lifetime is longer than the console token");
        });

        var asUser = Factory.CreateClient();
        asUser.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token!.Token);
        var me = await asUser.GetFromJsonAsync<CurrentPrincipal>("/auth/me");
        Assert.That(me!.PrincipalId, Is.EqualTo(principalId), "calls run AS the signed-in user");
    }

    [Test]
    public async Task Login_WithWrongPassword_IsUnauthorized()
    {
        await CreateHumanAsync("jane2", "correct-password");

        var response = await Factory.CreateClient().PostAsJsonAsync("/auth/login",
            new PasswordLoginRequest("jane2", "wrong-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Login_DisabledPrincipal_IsUnauthorized()
    {
        var principalId = await CreateHumanAsync("jane3", "hunter2-jane3");
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        await directory.SetEnabledAsync(principalId, enabled: false);

        var response = await Factory.CreateClient().PostAsJsonAsync("/auth/login",
            new PasswordLoginRequest("jane3", "hunter2-jane3"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "a disabled principal must not sign in");
    }
}

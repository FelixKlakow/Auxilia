using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// POST /auth/login is rate-limited two ways: a fixed window per client IP (all attempts), and a
/// per-username failure throttle that survives IP rotation. Both refusals are 429 — brute-forcing
/// passwords must never be free. These fixtures lower the limits to keep the tests fast.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class AuthLoginPerIpRateLimitTests : CoreApiComponentTestBase
{
    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.UseSetting("CoreSecurity:LoginRateLimitPermitsPerMinute", "3");

    [Test]
    public async Task Login_ExceedingThePerIpWindow_Returns429()
    {
        var client = CreateAnonymousClient();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
            last = await client.PostAsJsonAsync("/auth/login", new PasswordLoginRequest("nobody", "wrong"));

        Assert.That(last!.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests),
            "the 4th attempt from the same client within the window must be rate-limited");
    }
}

[TestFixture]
[Category("Component")]
public sealed class AuthLoginPerUsernameThrottleTests : CoreApiComponentTestBase
{
    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        // Per-IP window wide open so this fixture exercises ONLY the per-username throttle.
        builder.UseSetting("CoreSecurity:LoginRateLimitPermitsPerMinute", "100");
        builder.UseSetting("CoreSecurity:LoginFailureLimitPerUsername", "2");
    }

    [Test]
    public async Task Login_AfterRepeatedFailuresForAUsername_Refuses_EvenTheCorrectPassword()
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        await directory.CreateHumanAsync("Jane Desktop", "jane", "correct-password");
        var client = CreateAnonymousClient();

        for (var i = 0; i < 2; i++)
        {
            var denied = await client.PostAsJsonAsync("/auth/login", new PasswordLoginRequest("jane", "wrong"));
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        var throttled = await client.PostAsJsonAsync("/auth/login", new PasswordLoginRequest("jane", "correct-password"));
        var other = await client.PostAsJsonAsync("/auth/login", new PasswordLoginRequest("someone-else", "whatever"));

        Assert.Multiple(() =>
        {
            Assert.That(throttled.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests),
                "once throttled, even the correct password is refused — an attacker learns nothing");
            Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "a different username is not collaterally locked");
        });
    }

    [Test]
    public async Task Login_Success_ResetsTheFailureCount()
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        await directory.CreateHumanAsync("Jane Desktop", "jane", "correct-password");
        var client = CreateAnonymousClient();

        await client.PostAsJsonAsync("/auth/login", new PasswordLoginRequest("jane", "wrong"));
        var success = await client.PostAsJsonAsync("/auth/login", new PasswordLoginRequest("jane", "correct-password"));
        Assert.That(success.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var afterReset = await client.PostAsJsonAsync("/auth/login", new PasswordLoginRequest("jane", "wrong"));
        Assert.That(afterReset.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "the slate is clean after a successful sign-in — a single new failure is a 401, not a 429");
    }
}

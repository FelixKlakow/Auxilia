using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Runtime platform settings: writes demand policy.administer PLUS a fresh step-up elevation,
/// unknown keys and bad values are rejected, and /auth/login honors the stored lifetime.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class PlatformSettingsTests : CoreApiComponentTestBase
{
    private async Task<HttpClient> ElevatedAdminAsync()
    {
        var admin = CreateClient();
        var stepUp = await admin.PostAsJsonAsync("/auth/step-up", new StepUpRequest(TestApiKey));
        var ticket = await stepUp.Content.ReadFromJsonAsync<ElevationTicket>();
        admin.DefaultRequestHeaders.Add("X-Auxilia-Elevation", ticket!.Token);
        return admin;
    }

    [Test]
    public async Task Set_WithoutElevation_IsRejectedWithElevationRequired()
    {
        var admin = CreateClient();

        var response = await admin.PutAsJsonAsync(
            $"/api/platform-settings/{PlatformSettingKeys.LoginTokenLifetimeMinutes}",
            new SetPlatformSetting("60"));

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("elevation-required"),
                "security knobs demand a fresh step-up, like admin-role grants");
        });
    }

    [Test]
    public async Task Set_Elevated_Persists_AndLoginHonorsTheStoredLifetime()
    {
        var admin = await ElevatedAdminAsync();
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        await directory.CreateHumanAsync("Settings Tester", "settings.tester", "settings-pw");

        var set = await admin.PutAsJsonAsync(
            $"/api/platform-settings/{PlatformSettingKeys.LoginTokenLifetimeMinutes}",
            new SetPlatformSetting("60"));
        Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var login = await Factory.CreateClient().PostAsJsonAsync("/auth/login",
            new PasswordLoginRequest("settings.tester", "settings-pw"));
        var token = await login.Content.ReadFromJsonAsync<UserBearerToken>();
        Assert.That(token!.ExpiresUtc, Is.EqualTo(DateTimeOffset.UtcNow.AddMinutes(60)).Within(TimeSpan.FromMinutes(2)),
            "the runtime setting governs the login lifetime — no restart involved");

        var listed = await admin.GetFromJsonAsync<IReadOnlyList<PlatformSettingDto>>("/api/platform-settings");
        Assert.That(listed!.Single(s => s.Key == PlatformSettingKeys.LoginTokenLifetimeMinutes).Value,
            Is.EqualTo("60"));
    }

    [Test]
    public async Task Set_UnknownKeyOrBadValue_IsRejected()
    {
        var admin = await ElevatedAdminAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(
                (await admin.PutAsJsonAsync("/api/platform-settings/no-such-setting",
                    new SetPlatformSetting("1"))).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest), "a typo must fail loudly, not lie dormant");
            Assert.That(
                (await admin.PutAsJsonAsync(
                    $"/api/platform-settings/{PlatformSettingKeys.LoginTokenLifetimeMinutes}",
                    new SetPlatformSetting("zero"))).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task DefaultResourceAccess_AcceptsOnlyTheTwoModes_AndGovernsDispatch()
    {
        var admin = await ElevatedAdminAsync();

        Assert.That(
            (await admin.PutAsJsonAsync(
                $"/api/platform-settings/{PlatformSettingKeys.DefaultResourceAccess}",
                new SetPlatformSetting("everyone"))).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest), "only restricted|open are admissible");

        // Flip to open, and a User-role principal may trigger an ungranted type again.
        var set = await admin.PutAsJsonAsync(
            $"/api/platform-settings/{PlatformSettingKeys.DefaultResourceAccess}",
            new SetPlatformSetting(DefaultResourceAccessModes.Open));
        Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await RegisterActiveTypeAsync(CreateClient(), "settings-open-wt", "docker://img");
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}");
        await directory.AssignRoleAsync(principal.Id, BuiltInRoles.User);
        var user = Factory.CreateClient();
        user.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        Assert.That(
            (await user.PostAsJsonAsync("/api/runs", new { workflowType = "settings-open-wt" })).StatusCode,
            Is.EqualTo(HttpStatusCode.OK), "the open posture restores role-governed dispatch");

        // Back to restricted: the same principal is refused again — no restart involved.
        await admin.PutAsJsonAsync(
            $"/api/platform-settings/{PlatformSettingKeys.DefaultResourceAccess}",
            new SetPlatformSetting(DefaultResourceAccessModes.Restricted));
        Assert.That(
            (await user.PostAsJsonAsync("/api/runs", new { workflowType = "settings-open-wt" })).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Reads_WithoutPolicyAdminister_AreForbidden()
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}");
        await directory.AssignRoleAsync(principal.Id, "User");
        var user = Factory.CreateClient();
        user.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var response = await user.GetAsync("/api/platform-settings");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}

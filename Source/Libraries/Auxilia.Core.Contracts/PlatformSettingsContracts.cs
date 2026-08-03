namespace Auxilia.Core.Contracts;

/// <summary>
/// One runtime platform setting: security/behavior knobs an administrator changes WITHOUT a
/// redeployment. Reads require <c>policy.administer</c>; writes additionally demand a step-up
/// elevation (they shape the security posture). An unset key falls back to the deployment
/// configuration default.
/// </summary>
public sealed record PlatformSettingDto(string Key, string Value, DateTimeOffset UpdatedUtc);

/// <summary>Sets (or overwrites) one platform setting's value.</summary>
public sealed record SetPlatformSetting(string Value);

/// <summary>The known platform-setting keys (an open vocabulary — unknown keys are rejected).</summary>
public static class PlatformSettingKeys
{
    /// <summary>
    /// Minutes a <c>POST /auth/login</c> per-user bearer lives. Unset falls back to
    /// <c>CoreSecurity:LoginTokenLifetimeMinutes</c> (default one week).
    /// </summary>
    public const string LoginTokenLifetimeMinutes = "auth.login-token-lifetime-minutes";
}

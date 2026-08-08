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

    /// <summary>
    /// Who may use a grantable resource (workflow type, environment layer, slot provider) that
    /// carries NO explicit grants: <see cref="DefaultResourceAccessModes.Restricted"/> (the
    /// default — administrators only) or <see cref="DefaultResourceAccessModes.Open"/> (every
    /// authenticated principal, the pre-hardening behavior). Explicit grants override either
    /// way; administrators always pass.
    /// </summary>
    public const string DefaultResourceAccess = "security.default-resource-access";
}

/// <summary>The admissible values of <see cref="PlatformSettingKeys.DefaultResourceAccess"/>.</summary>
public static class DefaultResourceAccessModes
{
    /// <summary>An ungranted resource is usable by administrators only (the default).</summary>
    public const string Restricted = "restricted";
    /// <summary>An ungranted resource is usable by every authenticated principal.</summary>
    public const string Open = "open";
}

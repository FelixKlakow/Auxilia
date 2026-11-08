namespace Auxilia.Core.Contracts;

/// <summary>
/// One runtime platform setting: security/behavior knobs an administrator changes WITHOUT a
/// redeployment. Reads require <c>policy.administer</c>; writes additionally demand a step-up
/// elevation (they shape the security posture). An unset key falls back to the deployment
/// configuration default.
/// </summary>
public sealed record PlatformSettingDto(string Key, string Value, DateTimeOffset UpdatedUtc);

/// <summary>Sets (or overwrites) one platform setting's value; an empty value clears it back to the deployment default.</summary>
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

    /// <summary>
    /// Hours a run checkpoint is kept after its latest save — the GENERAL lifetime, used unless a
    /// workflow's declaration or the run request sets one and <see cref="CheckpointLifetimeOverrides"/>
    /// lets it. Unset falls back to <c>CoreApi:CheckpointLifetimeHours</c> (default 168, a week).
    /// </summary>
    public const string CheckpointLifetimeHours = "artifacts.checkpoint-lifetime-hours";

    /// <summary>
    /// Who may set a checkpoint lifetime other than the general one, from the most specific level
    /// down (<see cref="CheckpointLifetimeOverrideModes"/>): the run request, the workflow, or nobody.
    /// Unset reads as <see cref="CheckpointLifetimeOverrideModes.Run"/>.
    /// </summary>
    public const string CheckpointLifetimeOverrides = "artifacts.checkpoint-lifetime-overrides";

    /// <summary>
    /// The longest any checkpoint may be kept, in hours, whoever sets it — unset falls back to
    /// <c>CoreApi:CheckpointLifetimeMaxHours</c> (default 720, thirty days). A run request above it is
    /// refused; a workflow declaration or the general lifetime above it is cut to it.
    /// </summary>
    public const string CheckpointLifetimeMaxHours = "artifacts.checkpoint-lifetime-max-hours";
}

/// <summary>The admissible values of <see cref="PlatformSettingKeys.CheckpointLifetimeOverrides"/>.</summary>
public static class CheckpointLifetimeOverrideModes
{
    /// <summary>A run request's lifetime overrides the workflow's, which overrides the general one (the default).</summary>
    public const string Run = "run";
    /// <summary>A workflow's declared lifetime overrides the general one; run requests may not set one.</summary>
    public const string Workflow = "workflow";
    /// <summary>The general lifetime applies to every checkpoint; neither workflows nor runs may set one.</summary>
    public const string None = "none";
}

/// <summary>The admissible values of <see cref="PlatformSettingKeys.DefaultResourceAccess"/>.</summary>
public static class DefaultResourceAccessModes
{
    /// <summary>An ungranted resource is usable by administrators only (the default).</summary>
    public const string Restricted = "restricted";
    /// <summary>An ungranted resource is usable by every authenticated principal.</summary>
    public const string Open = "open";
}

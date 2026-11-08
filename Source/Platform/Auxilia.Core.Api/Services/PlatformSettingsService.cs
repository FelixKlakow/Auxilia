using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Runtime platform settings: security knobs an administrator changes without a redeployment
/// (gated <c>policy.administer</c> + step-up elevation at the endpoint). Known keys only — a
/// typo must fail loudly, not lie dormant. Unset keys fall back to deployment configuration — an
/// empty value clears a key back to it; every write is audited.
/// </summary>
public sealed class PlatformSettingsService(
    IDataAccess<PlatformSettingRecord> store,
    IOptions<CoreSecuritySettings> security,
    AuditLog auditLog,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<PlatformSettingDto>> ListAsync(CancellationToken ct)
        => (await store.ReadAsync(ct))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new PlatformSettingDto(s.Key, s.Value, s.UpdatedUtc))
            .ToList();

    public async Task<PlatformSettingDto> SetAsync(Guid? actor, string key, string value, CancellationToken ct)
    {
        key = key?.Trim() ?? "";
        value = value?.Trim() ?? "";
        if (value.Length == 0)
        {
            if (!KnownKeys.Contains(key))
                throw new ArgumentException($"unknown platform setting '{key}'");
            await store.RemoveAsync(PlatformSettingRecord.IdFor(key), ct);
            await auditLog.AppendAsync(
                actor?.ToString("D") ?? "core-api", "platform-settings.cleared", key, "deployment default", ct: ct);
            return new PlatformSettingDto(key, "", clock.GetUtcNow());
        }
        Validate(key, value);
        var record = new PlatformSettingRecord
        {
            Id = PlatformSettingRecord.IdFor(key),
            Key = key,
            Value = value,
            UpdatedUtc = clock.GetUtcNow(),
            UpdatedBy = actor,
        };
        await store.SaveAsync(record, ct);
        await auditLog.AppendAsync(
            actor?.ToString("D") ?? "core-api", "platform-settings.updated", key, value, ct: ct);
        return new PlatformSettingDto(record.Key, record.Value, record.UpdatedUtc);
    }

    /// <summary>The stored value of a known setting; null when unset (the caller applies its fallback).</summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct)
        => (await store.ReadAsync(PlatformSettingRecord.IdFor(key), ct))?.Value;

    /// <summary>The /auth/login bearer lifetime: the stored setting, else the configured default.</summary>
    public async Task<TimeSpan> GetLoginTokenLifetimeAsync(CancellationToken ct)
    {
        var stored = await store.ReadAsync(
            PlatformSettingRecord.IdFor(PlatformSettingKeys.LoginTokenLifetimeMinutes), ct);
        var minutes = stored is not null && int.TryParse(stored.Value, out var value) && value > 0
            ? value
            : Math.Max(1, security.Value.LoginTokenLifetimeMinutes);
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>
    /// Whether an ungranted resource is restricted to administrators. Unset (and anything
    /// unparseable) reads as restricted — the maximum-security default; only an explicit
    /// <see cref="DefaultResourceAccessModes.Open"/> opens the platform up.
    /// </summary>
    public async Task<bool> IsDefaultResourceAccessRestrictedAsync(CancellationToken ct)
    {
        var stored = await store.ReadAsync(
            PlatformSettingRecord.IdFor(PlatformSettingKeys.DefaultResourceAccess), ct);
        return !string.Equals(
            stored?.Value, DefaultResourceAccessModes.Open, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> KnownKeys =
    [
        PlatformSettingKeys.LoginTokenLifetimeMinutes,
        PlatformSettingKeys.DefaultResourceAccess,
        PlatformSettingKeys.CheckpointLifetimeHours,
        PlatformSettingKeys.CheckpointLifetimeOverrides,
        PlatformSettingKeys.CheckpointLifetimeMaxHours,
    ];

    private static void Validate(string key, string value)
    {
        switch (key)
        {
            case PlatformSettingKeys.LoginTokenLifetimeMinutes:
                if (!int.TryParse(value, out var minutes) || minutes < 1)
                    throw new ArgumentException("the login-token lifetime must be a positive number of minutes");
                return;
            case PlatformSettingKeys.DefaultResourceAccess:
                if (!string.Equals(value, DefaultResourceAccessModes.Restricted, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(value, DefaultResourceAccessModes.Open, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        $"default-resource-access must be '{DefaultResourceAccessModes.Restricted}' "
                        + $"or '{DefaultResourceAccessModes.Open}'");
                return;
            case PlatformSettingKeys.CheckpointLifetimeHours:
            case PlatformSettingKeys.CheckpointLifetimeMaxHours:
                if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var hours)
                    || !CheckpointLifetimePolicy.IsValidHours(hours))
                    throw new ArgumentException(
                        $"{key} must be a positive number of hours, at most {CheckpointLifetimePolicy.MaxHours}");
                return;
            case PlatformSettingKeys.CheckpointLifetimeOverrides:
                if (value.ToLowerInvariant() is not (CheckpointLifetimeOverrideModes.Run
                    or CheckpointLifetimeOverrideModes.Workflow or CheckpointLifetimeOverrideModes.None))
                    throw new ArgumentException(
                        $"{key} must be '{CheckpointLifetimeOverrideModes.Run}', "
                        + $"'{CheckpointLifetimeOverrideModes.Workflow}' or '{CheckpointLifetimeOverrideModes.None}'");
                return;
            default:
                throw new ArgumentException($"unknown platform setting '{key}'");
        }
    }
}

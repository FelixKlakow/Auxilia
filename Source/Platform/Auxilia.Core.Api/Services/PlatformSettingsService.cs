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
/// typo must fail loudly, not lie dormant. Unset keys fall back to deployment configuration;
/// every write is audited.
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

    private static void Validate(string key, string value)
    {
        switch (key)
        {
            case PlatformSettingKeys.LoginTokenLifetimeMinutes:
                if (!int.TryParse(value, out var minutes) || minutes < 1)
                    throw new ArgumentException("the login-token lifetime must be a positive number of minutes");
                return;
            default:
                throw new ArgumentException($"unknown platform setting '{key}'");
        }
    }
}

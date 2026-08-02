using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance.IdentityImport;

/// <summary>Result counts of one import run; persisted on the source record and audited.</summary>
public sealed record IdentityImportSummary(
    int Created,
    int Updated,
    int Disabled,
    int Skipped,
    IReadOnlyList<string> Warnings)
{
    public override string ToString()
        => $"{Created} created · {Updated} updated · {Disabled} disabled · {Skipped} skipped";
}

/// <summary>One identity source as shown to admin UIs — secret setting values never leave the service.</summary>
public sealed record IdentitySourceView(
    Guid Id,
    string Name,
    string ConnectorType,
    bool DisableMissing,
    string DefaultRole,
    IReadOnlyDictionary<string, string> GroupRoleMappings,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlySet<string> StoredSecretKeys,
    IdentityImportSummary? LastImport,
    DateTimeOffset? LastImportUtc);

/// <summary>Mutable editing model for creating or updating an identity source.</summary>
public sealed class IdentitySourceDraft
{
    /// <summary>Natural-key name when editing an existing source; null when creating.</summary>
    public string? ExistingName { get; set; }

    public string Name { get; set; } = "";
    public string ConnectorType { get; set; } = "";
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);
    public string DefaultRole { get; set; } = "";
    public Dictionary<string, string> GroupRoleMappings { get; } = new(StringComparer.Ordinal);
    public bool DisableMissing { get; set; }
}

/// <summary>
/// Administration and execution of identity imports (#23). An import is an idempotent UPSERT:
/// principal IDs derive deterministically from (source, external ID), existing principals are
/// updated in place, users missing from the source are at most disabled (never deleted), and
/// role mappings only ever add assignments — manually assigned roles are never stripped.
/// Imported principals carry no local credentials: they sign in once an administrator sets
/// credentials (v1 boundary — no pass-through authentication against the source).
/// Every mutation and import run is audited, always without setting or credential values.
/// </summary>
public sealed class IdentityImportService(
    IDataAccess<IdentitySourceRecord> sources,
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<RoleAssignmentRecord> roleAssignments,
    IEnumerable<IIdentityImportConnector> connectors,
    ISettingsProtector protector,
    AuditLog auditLog,
    TimeProvider timeProvider)
{
    private const string RoleAssignmentSource = "IdentityImport";

    private readonly IReadOnlyList<IIdentityImportConnector> _connectors = connectors.ToList();

    public IReadOnlyList<IIdentityImportConnector> Connectors => _connectors;

    // ------------------------------------------------------------------ sources

    public async Task<IReadOnlyList<IdentitySourceView>> ListAsync(CancellationToken ct = default)
    {
        var query = await sources.ReadAsync(ct);
        return query.ToList()
            .Select(ToView)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IdentitySourceView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var record = await sources.ReadAsync(id, ct);
        return record is null ? null : ToView(record);
    }

    /// <summary>
    /// Creates or updates a source. Secret settings are write-only: an empty secret input
    /// keeps the stored value. The name is the natural key and immutable after creation.
    /// </summary>
    public async Task<IdentitySourceView> SaveAsync(
        string actor, IdentitySourceDraft draft, CancellationToken ct = default)
    {
        var name = (draft.ExistingName ?? draft.Name).Trim();
        var connector = ValidateDraft(draft, name);

        var existing = await sources.ReadAsync(IdentitySourceRecord.IdFor(name), ct);
        if (draft.ExistingName is null && existing is not null)
            throw new ArgumentException($"An identity source named '{name}' already exists.");
        if (draft.ExistingName is not null && existing is null)
            throw new InvalidOperationException($"Unknown identity source '{name}'.");

        var settings = EffectiveSettings(draft, connector, existing);
        ValidateRequiredSettings(connector, settings);

        var record = new IdentitySourceRecord
        {
            Id = IdentitySourceRecord.IdFor(name),
            Name = name,
            ConnectorType = connector.ConnectorType,
            ProtectedSettingsJson = protector.Protect(JsonSerializer.Serialize(settings)),
            DefaultRole = draft.DefaultRole.Trim(),
            GroupRoleMappingsJson = JsonSerializer.Serialize(NormalizedMappings(draft)),
            DisableMissing = draft.DisableMissing,
            LastImportSummaryJson = existing?.LastImportSummaryJson,
            LastImportUtc = existing?.LastImportUtc
        };
        await sources.SaveAsync(record, ct);

        // Audited without settings values — only the configured key names.
        await auditLog.AppendAsync(actor, "identity-source.saved", record.Name,
            existing is null ? "created" : "updated",
            JsonSerializer.Serialize(new
            {
                connectorType = record.ConnectorType,
                settingKeys = settings.Keys.Order(StringComparer.Ordinal).ToList(),
                defaultRole = record.DefaultRole,
                groupMappings = NormalizedMappings(draft),
                disableMissing = record.DisableMissing
            }), ct);
        return ToView(record);
    }

    /// <summary>Deletes the source configuration; principals it imported stay untouched.</summary>
    public async Task<bool> DeleteAsync(string actor, Guid id, CancellationToken ct = default)
    {
        var record = await sources.ReadAsync(id, ct);
        if (record is null || !await sources.RemoveAsync(id, ct))
            return false;

        await auditLog.AppendAsync(actor, "identity-source.deleted", record.Name, "removed", ct: ct);
        return true;
    }

    // ------------------------------------------------------------------ actions

    public async Task<ConnectorTestResult> TestConnectionAsync(Guid id, CancellationToken ct = default)
    {
        var record = await sources.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("Unknown identity source.");
        var connector = ConnectorOf(record.ConnectorType);
        return await connector.TestConnectionAsync(UnprotectSettings(record.ProtectedSettingsJson), ct);
    }

    /// <summary>Runs the import for one source and returns (and persists) its summary.</summary>
    public async Task<IdentityImportSummary> ImportAsync(
        string actor, Guid id, CancellationToken ct = default)
    {
        var record = await sources.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("Unknown identity source.");
        var connector = ConnectorOf(record.ConnectorType);
        var mappings = ParseMappings(record.GroupRoleMappingsJson);

        var fetch = await connector.FetchUsersAsync(UnprotectSettings(record.ProtectedSettingsJson), ct);

        var created = 0;
        var updated = 0;
        var disabled = 0;
        var skipped = fetch.SkippedEntries.Count;
        var warnings = new List<string>(fetch.SkippedEntries);

        var subjectPrefix = ExternalSubjectPrefix(record.Id);
        var seenIds = new HashSet<Guid>();

        foreach (var user in fetch.Users)
        {
            ct.ThrowIfCancellationRequested();

            var principalId = PrincipalIdFor(record.Id, user.ExternalId);
            if (!seenIds.Add(principalId))
            {
                skipped++;
                warnings.Add($"duplicate external ID '{user.ExternalId}' — first occurrence wins");
                continue;
            }

            var desiredStatus = user.Enabled ? "Active" : "Disabled";
            var existing = await principals.ReadAsync(principalId, ct);
            if (existing is null)
            {
                // External principal: source-marked, no local password, no API key — an
                // administrator sets credentials before the user can sign in (v1 boundary).
                await principals.SaveAsync(new PrincipalRecord
                {
                    Id = principalId,
                    TenantId = Tenants.DefaultTenantId,
                    Kind = "Human",
                    DisplayName = user.DisplayName,
                    ExternalSubject = subjectPrefix + user.ExternalId,
                    Status = desiredStatus
                }, ct);
                created++;
                await AuditAppliedAsync(actor, principalId, user, "created", ct);
            }
            else if (existing.DisplayName != user.DisplayName || existing.Status != desiredStatus)
            {
                await principals.SaveAsync(existing with
                {
                    DisplayName = user.DisplayName,
                    Status = desiredStatus
                }, ct);
                updated++;
                await AuditAppliedAsync(actor, principalId, user, "updated", ct);
            }
            else
            {
                skipped++; // unchanged — idempotent re-import
            }

            await ApplyRolesAsync(actor, principalId, user, record.DefaultRole, mappings, ct);
        }

        // Users that disappeared from the source: disable only when configured — NEVER delete.
        if (record.DisableMissing)
        {
            var query = await principals.ReadAsync(ct);
            var missing = query
                .Where(p => p.ExternalSubject != null && p.ExternalSubject.StartsWith(subjectPrefix))
                .ToList()
                .Where(p => !seenIds.Contains(p.Id) && p.Status == "Active")
                .ToList();
            foreach (var principal in missing)
            {
                await principals.SaveAsync(principal with { Status = "Disabled" }, ct);
                disabled++;
                await auditLog.AppendAsync(actor, "identity-import.applied", principal.Id.ToString("D"),
                    "disabled", JsonSerializer.Serialize(new { reason = "missing-from-source" }), ct);
            }
        }

        var summary = new IdentityImportSummary(created, updated, disabled, skipped, warnings);
        await sources.SaveAsync(record with
        {
            LastImportSummaryJson = JsonSerializer.Serialize(summary),
            LastImportUtc = timeProvider.GetUtcNow()
        }, ct);

        await auditLog.AppendAsync(actor, "identity-import.run", record.Name, summary.ToString(),
            JsonSerializer.Serialize(new { created, updated, disabled, skipped, warnings }), ct);
        return summary;
    }

    /// <summary>Deterministic principal ID: imports from any replica converge on the same record.</summary>
    public static Guid PrincipalIdFor(Guid sourceId, string externalId)
        => DeterministicGuid.For("identity-import", sourceId.ToString("D"), externalId);

    internal static string ExternalSubjectPrefix(Guid sourceId) => $"identity-source:{sourceId:D}:";

    // ------------------------------------------------------------------ internals

    private async Task ApplyRolesAsync(
        string actor, Guid principalId, ExternalUser user, string defaultRole,
        IReadOnlyDictionary<string, string> mappings, CancellationToken ct)
    {
        var wanted = new List<string>();
        if (defaultRole.Length > 0)
            wanted.Add(defaultRole);
        wanted.AddRange(user.Groups
            .Select(group => mappings.GetValueOrDefault(group))
            .OfType<string>());

        foreach (var role in wanted.Where(BuiltInRoles.Exists).Distinct(StringComparer.Ordinal))
        {
            // Assign only when missing; existing assignments (manual or imported) stay as
            // they are — imports never strip roles.
            var assignmentId = RoleAssignmentRecord.IdFor(principalId, role);
            if (await roleAssignments.ReadAsync(assignmentId, ct) is not null)
                continue;

            await roleAssignments.SaveAsync(new RoleAssignmentRecord
            {
                Id = assignmentId,
                PrincipalId = principalId,
                RoleName = role,
                Source = RoleAssignmentSource
            }, ct);
            await auditLog.AppendAsync(actor, "role.assigned", principalId.ToString("D"), role, ct: ct);
        }
    }

    private Task AuditAppliedAsync(
        string actor, Guid principalId, ExternalUser user, string outcome, CancellationToken ct)
        // Never carries settings or credentials — only directory-public identity fields.
        => auditLog.AppendAsync(actor, "identity-import.applied", principalId.ToString("D"), outcome,
            JsonSerializer.Serialize(new { user.ExternalId, user.Username, user.Enabled }), ct);

    private IIdentityImportConnector ValidateDraft(IdentitySourceDraft draft, string name)
    {
        var errors = new List<string>();
        if (name.Length == 0)
            errors.Add("Name is required.");
        if (draft.DefaultRole.Trim().Length > 0 && !BuiltInRoles.Exists(draft.DefaultRole.Trim()))
            errors.Add($"Unknown default role '{draft.DefaultRole.Trim()}'.");
        foreach (var (group, role) in NormalizedMappings(draft))
        {
            if (!BuiltInRoles.Exists(role))
                errors.Add($"Group '{group}' maps to unknown role '{role}'.");
        }

        var connector = _connectors.FirstOrDefault(c => c.ConnectorType == draft.ConnectorType);
        if (connector is null)
            errors.Add(draft.ConnectorType.Length == 0
                ? "Pick a connector type."
                : $"Unknown connector type '{draft.ConnectorType}'.");

        if (errors.Count > 0)
            throw new ArgumentException(string.Join("\n", errors));
        return connector!;
    }

    private static void ValidateRequiredSettings(
        IIdentityImportConnector connector, IReadOnlyDictionary<string, string> settings)
    {
        var missing = connector.SettingDescriptors
            .Where(d => d.Required && string.IsNullOrWhiteSpace(settings.GetValueOrDefault(d.Key)))
            .Select(d => $"'{d.Label}' is required.")
            .ToList();
        if (missing.Count > 0)
            throw new ArgumentException(string.Join("\n", missing));
    }

    /// <summary>Non-empty draft values win; empty secret inputs keep the stored value; defaults fill the rest.</summary>
    private Dictionary<string, string> EffectiveSettings(
        IdentitySourceDraft draft, IIdentityImportConnector connector, IdentitySourceRecord? existing)
    {
        var settings = draft.Settings
            .Where(s => !string.IsNullOrEmpty(s.Value))
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

        if (existing is not null)
        {
            var secretKeys = SecretKeysOf(connector);
            foreach (var (key, value) in UnprotectSettings(existing.ProtectedSettingsJson))
            {
                if (secretKeys.Contains(key) && !settings.ContainsKey(key) && !string.IsNullOrEmpty(value))
                    settings[key] = value; // keep-unchanged: the admin typed nothing new
            }
        }

        foreach (var descriptor in connector.SettingDescriptors)
        {
            if (!settings.ContainsKey(descriptor.Key) && descriptor.DefaultValue is { Length: > 0 } fallback)
                settings[descriptor.Key] = fallback;
        }

        return settings;
    }

    private IdentitySourceView ToView(IdentitySourceRecord record)
    {
        var secretKeys = _connectors.FirstOrDefault(c => c.ConnectorType == record.ConnectorType) is { } connector
            ? SecretKeysOf(connector)
            : new HashSet<string>(StringComparer.Ordinal);

        var settings = UnprotectSettings(record.ProtectedSettingsJson);
        var visible = new Dictionary<string, string>(StringComparer.Ordinal);
        var stored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            if (secretKeys.Contains(key))
            {
                visible[key] = ""; // write-only: never returned
                if (!string.IsNullOrEmpty(value))
                    stored.Add(key);
            }
            else
            {
                visible[key] = value;
            }
        }

        return new IdentitySourceView(
            record.Id, record.Name, record.ConnectorType, record.DisableMissing, record.DefaultRole,
            ParseMappings(record.GroupRoleMappingsJson), visible, stored,
            ParseSummary(record.LastImportSummaryJson), record.LastImportUtc);
    }

    private IIdentityImportConnector ConnectorOf(string connectorType)
        => _connectors.FirstOrDefault(c => c.ConnectorType == connectorType)
           ?? throw new InvalidOperationException($"No connector of type '{connectorType}' is registered.");

    private static HashSet<string> SecretKeysOf(IIdentityImportConnector connector)
        => connector.SettingDescriptors
            .Where(d => d.Kind == ConnectorSettingKind.Secret)
            .Select(d => d.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> NormalizedMappings(IdentitySourceDraft draft)
        => draft.GroupRoleMappings
            .Where(m => m.Key.Trim().Length > 0 && m.Value.Trim().Length > 0)
            .ToDictionary(m => m.Key.Trim(), m => m.Value.Trim(), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> ParseMappings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static IdentityImportSummary? ParseSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<IdentityImportSummary>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Dictionary<string, string> UnprotectSettings(string protectedJson)
    {
        if (string.IsNullOrWhiteSpace(protectedJson))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(protectedJson))
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            // Wrong key or foreign format — behave as if no settings were stored.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}

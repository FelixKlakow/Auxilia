using Auxilia.BackendService.Dashboard.Connect;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.BackendService.Dashboard;

/// <summary>One connector without its credential — everything list UIs may show.</summary>
public sealed record ConnectorOverview(
    Guid Id,
    string Name,
    string DisplayName,
    string FlowKey,
    string FlowDisplayName,
    string Scope,
    Guid? OwnerPrincipalId,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// The connectors surface: signed-in external accounts (Claude, GitHub, …) created once on
/// the connectors page and picked from wherever a slot setting names the same connect flow.
/// Tokens are protected at rest, write-only, audited without values, and only leave this
/// service through the access-checked <see cref="ResolveTokenAsync"/>.
/// </summary>
public sealed class ConnectorService(
    IDataAccess<ConnectorRecord> connectors,
    ConnectFlowRegistry flows,
    ISettingsProtector protector,
    AuditLog auditLog,
    TimeProvider time)
{
    /// <summary>Marker a slot setting carries instead of a raw secret when a connector was picked.</summary>
    public const string ReferencePrefix = "@connector:";

    public static string ReferenceFor(Guid id) => $"{ReferencePrefix}{id:D}";

    public static Guid? ReferencedId(string? settingValue)
        => settingValue is not null
           && settingValue.StartsWith(ReferencePrefix, StringComparison.Ordinal)
           && Guid.TryParse(settingValue[ReferencePrefix.Length..], out var id)
            ? id
            : null;

    public async Task<IReadOnlyList<ConnectorOverview>> ListAsync(CancellationToken ct = default)
        => (await connectors.ReadAsync(ct)).ToList()
            .Select(ToOverview)
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The connectors <paramref name="principalId"/> may pick: company-scoped plus their own.</summary>
    public async Task<IReadOnlyList<ConnectorOverview>> AccessibleAsync(
        Guid principalId, string? flowKey = null, CancellationToken ct = default)
        => (await ListAsync(ct))
            .Where(c => c.Scope == SlotInstanceScope.Company || c.OwnerPrincipalId == principalId)
            .Where(c => flowKey is null || c.FlowKey == flowKey)
            .ToList();

    /// <summary>
    /// Creates or updates a connector. An empty <paramref name="token"/> on update keeps the
    /// stored credential (write-only semantics); on create it is refused.
    /// </summary>
    public async Task<string> SaveAsync(
        string actor, Guid? ownerPrincipalId, string? existingName, string displayName,
        string flowKey, string? token, string scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.");
        if (flows.Find(flowKey) is null)
            throw new ArgumentException($"Unknown connect flow '{flowKey}'.");
        if (scope is not (SlotInstanceScope.Company or SlotInstanceScope.Personal))
            throw new ArgumentException("Scope must be Company or Personal.");

        var existing = existingName is null
            ? null
            : await connectors.ReadAsync(ConnectorRecord.IdFor(existingName), ct)
              ?? throw new InvalidOperationException("Unknown connector.");
        if (string.IsNullOrEmpty(token) && existing is null)
            throw new ArgumentException("Finish the sign-in first — a new connector needs its credential.");

        var name = existingName ?? await NewUniqueNameAsync(displayName, ct);
        var now = time.GetUtcNow();
        await connectors.SaveAsync(new ConnectorRecord
        {
            Id = ConnectorRecord.IdFor(name),
            Name = name,
            DisplayName = displayName.Trim(),
            FlowKey = flowKey,
            ProtectedToken = string.IsNullOrEmpty(token) ? existing!.ProtectedToken : protector.Protect(token),
            Scope = scope,
            OwnerPrincipalId = ownerPrincipalId ?? existing?.OwnerPrincipalId,
            CreatedUtc = existing?.CreatedUtc ?? now,
            UpdatedUtc = now
        }, ct);

        // Audited without the credential: name, flow, and scope only.
        await auditLog.AppendAsync(actor, "connector.saved", name,
            existing is null ? "created" : "updated",
            System.Text.Json.JsonSerializer.Serialize(new { flowKey, scope }), ct);
        return name;
    }

    public async Task DeleteAsync(string actor, Guid id, CancellationToken ct = default)
    {
        var record = await connectors.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("Unknown connector.");
        await connectors.RemoveAsync(id, ct);
        await auditLog.AppendAsync(actor, "connector.deleted", record.Name, "removed", ct: ct);
    }

    /// <summary>
    /// Returns the credential of <paramref name="id"/> for embedding into a slot instance —
    /// only for company connectors or the caller's own.
    /// </summary>
    public async Task<string> ResolveTokenAsync(Guid id, Guid? principalId, CancellationToken ct = default)
    {
        var record = await connectors.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("The picked connector no longer exists.");
        if (record.Scope != SlotInstanceScope.Company && record.OwnerPrincipalId != principalId)
            throw new InvalidOperationException($"\"{record.DisplayName}\" is a personal connector of someone else.");
        return protector.Unprotect(record.ProtectedToken);
    }

    private ConnectorOverview ToOverview(ConnectorRecord record)
        => new(
            record.Id, record.Name, record.DisplayName, record.FlowKey,
            flows.Find(record.FlowKey)?.DisplayName ?? record.FlowKey,
            record.Scope, record.OwnerPrincipalId, record.UpdatedUtc);

    private async Task<string> NewUniqueNameAsync(string displayName, CancellationToken ct)
    {
        var slug = WorkflowConfigurationEditorService.Slugify(displayName);
        var existing = (await connectors.ReadAsync(ct)).ToList()
            .Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (!existing.Contains(slug))
            return slug;
        for (var i = 2; ; i++)
        {
            var candidate = $"{slug}-{i}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }
}

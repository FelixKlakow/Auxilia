using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Owns first-class repository/workspace resources. Settings are NON-secret (the credential
/// stays a connector reference), so reads return them — unlike connectors. Use in a run is
/// gated like connectors: Company usable by anyone, Personal by the owner and granted subjects.
/// </summary>
public sealed class WorkspaceResourceService(
    IDataAccess<CoreWorkspaceRecord> store,
    AccessGrantEvaluator grantEvaluator,
    TimeProvider clock)
{
    public async Task<WorkspaceResource> CreateAsync(
        CreateWorkspaceResource request, Guid? ownerPrincipalId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("name is required");
        if (string.IsNullOrWhiteSpace(request.ProviderType))
            throw new ArgumentException("providerType is required");
        var personal = request.Scope == ResourceScope.Personal;
        var record = new CoreWorkspaceRecord
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            ProviderType = request.ProviderType.Trim(),
            SettingsJson = JsonSerializer.Serialize(request.Settings ?? new Dictionary<string, string>()),
            ConnectorId = request.ConnectorId,
            Scope = personal ? ResourceScope.Personal : ResourceScope.Company,
            OwnerPrincipalId = personal ? ownerPrincipalId : null,
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(record, ct);
        return ToDto(record);
    }

    public async Task<WorkspaceResource?> UpdateAsync(
        Guid id, UpdateWorkspaceResource request, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } record)
            return null;
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("name is required");
        var updated = record with
        {
            Name = request.Name.Trim(),
            SettingsJson = JsonSerializer.Serialize(request.Settings ?? new Dictionary<string, string>()),
            ConnectorId = request.ConnectorId,
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(updated, ct);
        return ToDto(updated);
    }

    public async Task<bool> SetGrantsAsync(Guid id, IReadOnlyList<AccessGrant> grants, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } record)
            return false;
        await store.SaveAsync(
            record with { GrantsJson = JsonSerializer.Serialize(grants), UpdatedUtc = clock.GetUtcNow() }, ct);
        return true;
    }

    public async Task<WorkspaceResource?> GetAsync(Guid id, CancellationToken ct)
        => await store.ReadAsync(id, ct) is { } record ? ToDto(record) : null;

    /// <summary>Every repository the principal may SEE: company ones, own ones, granted ones.</summary>
    public async Task<IReadOnlyList<WorkspaceResource>> ListVisibleAsync(Guid? principalId, CancellationToken ct)
    {
        var visible = new List<WorkspaceResource>();
        foreach (var record in (await store.ReadAsync(ct))
                     .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            if (await CanUseRecordAsync(record, principalId, ct))
                visible.Add(ToDto(record));
        return visible;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        => await store.RemoveAsync(id, ct);

    /// <summary>Same gate as binding a connector: Company free, Personal owner-or-granted.</summary>
    public async Task<bool> CanUseAsync(Guid id, Guid? principalId, CancellationToken ct)
        => await store.ReadAsync(id, ct) is not { } record
           || await CanUseRecordAsync(record, principalId, ct);

    private async Task<bool> CanUseRecordAsync(CoreWorkspaceRecord record, Guid? principalId, CancellationToken ct)
    {
        if (record.Scope != ResourceScope.Personal)
            return true;
        if (principalId is not { } pid)
            return false;
        if (record.OwnerPrincipalId == pid)
            return true;
        return await grantEvaluator.IsGrantedAsync(record.GrantsJson, pid, ct);
    }

    private static WorkspaceResource ToDto(CoreWorkspaceRecord record)
        => new(
            record.Id, record.Name, record.ProviderType,
            JsonSerializer.Deserialize<Dictionary<string, string>>(record.SettingsJson)
                ?? new Dictionary<string, string>(),
            record.ConnectorId, record.UpdatedUtc, record.Scope, record.OwnerPrincipalId,
            JsonSerializer.Deserialize<List<AccessGrant>>(record.GrantsJson) ?? []);
}

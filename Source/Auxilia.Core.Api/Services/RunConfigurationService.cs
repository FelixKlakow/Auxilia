using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>The caller a configuration read is filtered for: managers see every configuration.</summary>
public readonly record struct ConfigurationViewer(Guid? PrincipalId, bool SeesAll);

/// <summary>
/// CRUD over Core-owned run configurations. A Personal configuration belongs to its owner and is
/// visible/runnable only to the owner, granted subjects (<see cref="AccessGrantEvaluator"/>), and
/// configuration managers; Company configurations are shared platform-wide.
/// </summary>
public sealed class RunConfigurationService(
    IDataAccess<CoreRunConfigurationRecord> store,
    AccessGrantEvaluator grants,
    TimeProvider clock)
{
    public async Task<RunConfiguration> CreateAsync(
        CreateRunConfiguration request, Guid? ownerPrincipalId, CancellationToken ct)
    {
        var personal = request.Scope == ResourceScope.Personal;
        var record = new CoreRunConfigurationRecord
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            WorkflowType = request.WorkflowType,
            ContextJson = JsonSerializer.Serialize(
                request.Context ?? new Dictionary<string, string>()),
            SlotBindingsJson = JsonSerializer.Serialize(
                request.SlotBindings ?? new List<SlotBinding>()),
            Enabled = request.Enabled,
            TagsJson = JsonSerializer.Serialize(request.Tags ?? []),
            Scope = personal ? ResourceScope.Personal : ResourceScope.Company,
            OwnerPrincipalId = personal ? ownerPrincipalId : null,
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(record, ct);
        return ToDto(record);
    }

    /// <summary>
    /// Updates a stored configuration: null request fields stay unchanged, provided ones replace
    /// the stored value wholesale. The workflow type, scope, and owner are immutable.
    /// </summary>
    public async Task<RunConfiguration?> UpdateAsync(Guid id, UpdateRunConfiguration request, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } record)
            return null;
        var updated = record with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? record.Name : request.Name,
            ContextJson = request.Context is null ? record.ContextJson : JsonSerializer.Serialize(request.Context),
            SlotBindingsJson = request.SlotBindings is null
                ? record.SlotBindingsJson
                : JsonSerializer.Serialize(request.SlotBindings),
            Enabled = request.Enabled ?? record.Enabled,
            TagsJson = request.Tags is null ? record.TagsJson : JsonSerializer.Serialize(request.Tags),
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(updated, ct);
        return ToDto(updated);
    }

    /// <summary>Replaces a personal configuration's access grants; company configurations have none.</summary>
    public async Task<RunConfiguration?> SetGrantsAsync(
        Guid id, IReadOnlyList<AccessGrant> newGrants, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } record)
            return null;
        if (record.Scope != ResourceScope.Personal)
            throw new InvalidOperationException("a company configuration is already shared — grants apply to personal configurations only");
        var updated = record with
        {
            GrantsJson = JsonSerializer.Serialize(newGrants),
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(updated, ct);
        return ToDto(updated);
    }

    /// <summary>Deletes a stored configuration permanently.</summary>
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        => store.RemoveAsync(id, ct);

    /// <summary>Unfiltered read — for Core-internal paths that already authorized the caller.</summary>
    public async Task<RunConfiguration?> GetAsync(Guid id, CancellationToken ct)
        => await store.ReadAsync(id, ct) is { } r ? ToDto(r) : null;

    /// <summary>Visibility-filtered read: an invisible configuration reads as not found.</summary>
    public async Task<RunConfiguration?> GetAsync(Guid id, ConfigurationViewer viewer, CancellationToken ct)
    {
        if (await store.ReadAsync(id, ct) is not { } record)
            return null;
        return await IsVisibleAsync(record, viewer, ct) ? ToDto(record) : null;
    }

    public async Task<PagedResult<RunConfiguration>> QueryAsync(
        ConfigurationQuery query, ConfigurationViewer viewer, CancellationToken ct)
    {
        var all = (await store.ReadAsync(ct)).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.WorkflowType))
            all = all.Where(c => c.WorkflowType == query.WorkflowType);
        if (query.Enabled is { } enabled)
            all = all.Where(c => c.Enabled == enabled);

        var visible = new List<CoreRunConfigurationRecord>();
        foreach (var record in all)
            if (await IsVisibleAsync(record, viewer, ct))
                visible.Add(record);

        var ordered = visible.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var page = ordered.Skip(query.Skip).Take(query.Take).Select(ToDto).ToList();
        return new PagedResult<RunConfiguration>(page, ordered.Count, query.Skip, query.Take);
    }

    /// <summary>Whether the viewer may see (and therefore run) this configuration.</summary>
    public async Task<bool> IsVisibleAsync(Guid id, ConfigurationViewer viewer, CancellationToken ct)
        => await store.ReadAsync(id, ct) is { } record && await IsVisibleAsync(record, viewer, ct);

    /// <summary>Whether the principal owns the configuration (personal scope only).</summary>
    public async Task<bool> IsOwnerAsync(Guid id, Guid? principalId, CancellationToken ct)
        => principalId is { } pid
           && await store.ReadAsync(id, ct) is { Scope: ResourceScope.Personal } record
           && record.OwnerPrincipalId == pid;

    private async Task<bool> IsVisibleAsync(
        CoreRunConfigurationRecord record, ConfigurationViewer viewer, CancellationToken ct)
    {
        if (record.Scope != ResourceScope.Personal || viewer.SeesAll)
            return true;
        if (viewer.PrincipalId is not { } pid)
            return false;
        if (record.OwnerPrincipalId == pid)
            return true;
        return await grants.IsGrantedAsync(record.GrantsJson, pid, ct);
    }

    /// <summary>Idempotently seeds a static COMPANY configuration; an existing name is left untouched.</summary>
    public async Task EnsureAsync(StaticRunConfiguration seed, CancellationToken ct)
    {
        var all = (await store.ReadAsync(ct)).AsEnumerable();
        if (all.Any(c => string.Equals(c.Name, seed.Name, StringComparison.OrdinalIgnoreCase)))
            return;
        await CreateAsync(
            new CreateRunConfiguration(seed.Name, seed.WorkflowType, seed.Context, Scope: ResourceScope.Company),
            ownerPrincipalId: null, ct);
    }

    internal static RunConfiguration ToDto(CoreRunConfigurationRecord r) => new(
        r.Id, r.Name, r.WorkflowType,
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.ContextJson)
            ?? new Dictionary<string, string>(),
        JsonSerializer.Deserialize<List<SlotBinding>>(r.SlotBindingsJson)
            ?? new List<SlotBinding>(),
        r.Enabled, r.UpdatedUtc,
        JsonSerializer.Deserialize<List<string>>(r.TagsJson ?? "[]") ?? [],
        r.Scope,
        r.OwnerPrincipalId,
        JsonSerializer.Deserialize<List<AccessGrant>>(r.GrantsJson ?? "[]") ?? []);
}

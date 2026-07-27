using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>CRUD over Core-owned run configurations (stored in the Core database).</summary>
public sealed class RunConfigurationService(
    IDataAccess<CoreRunConfigurationRecord> store,
    TimeProvider clock)
{
    public async Task<RunConfiguration> CreateAsync(CreateRunConfiguration request, CancellationToken ct)
    {
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
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(record, ct);
        return ToDto(record);
    }

    /// <summary>
    /// Updates a stored configuration: null request fields stay unchanged, provided ones replace
    /// the stored value wholesale. The workflow type is immutable.
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

    /// <summary>Deletes a stored configuration permanently.</summary>
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        => store.RemoveAsync(id, ct);

    public async Task<RunConfiguration?> GetAsync(Guid id, CancellationToken ct)
        => await store.ReadAsync(id, ct) is { } r ? ToDto(r) : null;

    public async Task<PagedResult<RunConfiguration>> QueryAsync(ConfigurationQuery query, CancellationToken ct)
    {
        var all = (await store.ReadAsync(ct)).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.WorkflowType))
            all = all.Where(c => c.WorkflowType == query.WorkflowType);
        if (query.Enabled is { } enabled)
            all = all.Where(c => c.Enabled == enabled);
        var ordered = all.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var page = ordered.Skip(query.Skip).Take(query.Take).Select(ToDto).ToList();
        return new PagedResult<RunConfiguration>(page, ordered.Count, query.Skip, query.Take);
    }

    /// <summary>Idempotently seeds a static configuration; a name that already exists is left untouched.</summary>
    public async Task EnsureAsync(StaticRunConfiguration seed, CancellationToken ct)
    {
        var all = (await store.ReadAsync(ct)).AsEnumerable();
        if (all.Any(c => string.Equals(c.Name, seed.Name, StringComparison.OrdinalIgnoreCase)))
            return;
        await CreateAsync(
            new CreateRunConfiguration(seed.Name, seed.WorkflowType, seed.Context),
            ct);
    }

    internal static RunConfiguration ToDto(CoreRunConfigurationRecord r) => new(
        r.Id, r.Name, r.WorkflowType,
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.ContextJson)
            ?? new Dictionary<string, string>(),
        JsonSerializer.Deserialize<List<SlotBinding>>(r.SlotBindingsJson)
            ?? new List<SlotBinding>(),
        r.Enabled, r.UpdatedUtc,
        JsonSerializer.Deserialize<List<string>>(r.TagsJson ?? "[]") ?? []);
}

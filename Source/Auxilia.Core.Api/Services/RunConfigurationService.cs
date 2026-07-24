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
            PackageUri = request.PackageUri,
            ContextJson = JsonSerializer.Serialize(
                request.Context ?? new Dictionary<string, string>()),
            SlotBindingsJson = JsonSerializer.Serialize(
                request.SlotBindings ?? new List<SlotBinding>()),
            Enabled = request.Enabled,
            UpdatedUtc = clock.GetUtcNow()
        };
        await store.SaveAsync(record, ct);
        return ToDto(record);
    }

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
            new CreateRunConfiguration(seed.Name, seed.WorkflowType, seed.PackageUri, seed.Context),
            ct);
    }

    internal static RunConfiguration ToDto(CoreRunConfigurationRecord r) => new(
        r.Id, r.Name, r.WorkflowType, r.PackageUri,
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.ContextJson)
            ?? new Dictionary<string, string>(),
        JsonSerializer.Deserialize<List<SlotBinding>>(r.SlotBindingsJson)
            ?? new List<SlotBinding>(),
        r.Enabled, r.UpdatedUtc);
}

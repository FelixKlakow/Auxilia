using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;

namespace Auxilia.BackendService.Dashboard;

/// <summary>Admin presentation override for one manifest-declared setting: label, help text, and default only — keys and kinds stay manifest-owned.</summary>
public sealed record SettingDescriptorOverride(
    string Key,
    string? Label = null,
    string? HelpText = null,
    string? DefaultValue = null);

/// <summary>One curated catalog entry: the provider registration joined with its admin curation; <see cref="Descriptors"/> already carries the overrides merged in.</summary>
public sealed record ProviderCatalogEntry(
    string ProviderType,
    string DllPath,
    bool Available,
    string Category,
    IReadOnlyList<SettingDescriptor> Descriptors,
    IReadOnlyList<SettingDescriptorOverride> Overrides,
    IReadOnlyList<string> Contracts,
    string? Description = null)
{
    /// <summary>Whether this provider declares it can back a slot expecting <paramref name="contract"/>; unclassified providers match nothing.</summary>
    public bool Implements(string contract)
        => Contracts.Contains(contract, StringComparer.Ordinal);
}

/// <summary>
/// Admin curation of the slot-provider catalog (#19): which registered providers users may
/// pick when configuring workflows, under which slot-kind category, and with which
/// presentation overrides. The manifest stays the source of truth for setting keys and
/// kinds; availability is deny-by-default. Every mutation is audited.
/// </summary>
public sealed class ProviderCatalogService(
    IDataAccess<SlotProviderRecord> providers,
    IDataAccess<ProviderCatalogRecord> catalog,
    AuditLog auditLog)
{
    /// <summary>All registered slot-handler plugins with their curation state (dependency-library registrations are not part of the catalog).</summary>
    public async Task<IReadOnlyList<ProviderCatalogEntry>> ListAsync(CancellationToken ct = default)
    {
        var providerQuery = await providers.ReadAsync(ct);
        var handlerRecords = providerQuery.ToList().Where(IsHandler).ToList();

        var entries = new List<ProviderCatalogEntry>();
        foreach (var record in handlerRecords)
            entries.Add(ToEntry(record, await catalog.ReadAsync(ProviderCatalogRecord.IdFor(record.ProviderType), ct)));
        return entries.OrderBy(e => e.ProviderType, StringComparer.Ordinal).ToList();
    }

    /// <summary>Read path for the workflow-configuration editor (#20): available providers with merged descriptors, grouped by category.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderCatalogEntry>>> ListAvailableByCategoryAsync(
        CancellationToken ct = default)
    {
        var entries = await ListAsync(ct);
        return entries
            .Where(e => e.Available)
            .GroupBy(e => e.Category, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ProviderCatalogEntry>)g.OrderBy(e => e.ProviderType, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
    }

    public async Task<ProviderCatalogEntry> SetAvailabilityAsync(
        string actor, string providerType, bool available, CancellationToken ct = default)
    {
        var (provider, curation) = await RequireProviderAsync(providerType, ct);
        var updated = curation with { Available = available };
        await catalog.SaveAsync(updated, ct);
        await auditLog.AppendAsync(actor, "provider-catalog.availability-changed",
            providerType, available ? "available" : "unavailable", ct: ct);
        return ToEntry(provider, updated);
    }

    /// <summary>Applies stored admin overrides onto the manifest descriptors; key, kind, required, and choices stay manifest-owned.</summary>
    internal static IReadOnlyList<SettingDescriptor> Merge(
        IReadOnlyList<SettingDescriptor> descriptors, IReadOnlyList<SettingDescriptorOverride> overrides)
    {
        var byKey = overrides.ToDictionary(o => o.Key, StringComparer.Ordinal);
        return descriptors
            .Select(d => !byKey.TryGetValue(d.Key, out var o)
                ? d
                : d with
                {
                    Label = string.IsNullOrWhiteSpace(o.Label) ? d.Label : o.Label,
                    HelpText = o.HelpText ?? d.HelpText,
                    DefaultValue = o.DefaultValue ?? d.DefaultValue
                })
            .ToList();
    }

    private async Task<(SlotProviderRecord Provider, ProviderCatalogRecord Curation)> RequireProviderAsync(
        string providerType, CancellationToken ct)
    {
        var provider = await providers.ReadAsync(SlotProviderRecord.IdFor(providerType), ct);
        if (provider is null || !IsHandler(provider))
            throw new InvalidOperationException($"'{providerType}' is not a registered slot-handler plugin.");

        var curation = await catalog.ReadAsync(ProviderCatalogRecord.IdFor(providerType), ct)
                       ?? NewCuration(providerType);
        return (provider, curation);
    }

    private static bool IsHandler(SlotProviderRecord provider)
        => provider.DllPath.EndsWith(".slothandler.dll", StringComparison.OrdinalIgnoreCase);

    private static ProviderCatalogRecord NewCuration(string providerType)
        => new()
        {
            Id = ProviderCatalogRecord.IdFor(providerType),
            ProviderType = providerType
        };

    private static ProviderCatalogEntry ToEntry(SlotProviderRecord provider, ProviderCatalogRecord? curation)
    {
        curation ??= NewCuration(provider.ProviderType);
        var overrides = ParseOverrides(curation.DescriptorOverridesJson);
        // The manifest-declared category is the default; the admin's curation wins when set.
        var category = curation.Category.Length > 0 ? curation.Category : provider.Category ?? "";
        return new ProviderCatalogEntry(
            provider.ProviderType,
            provider.DllPath,
            curation.Available,
            category,
            Merge(ParseDescriptors(provider.SettingDescriptorsJson), overrides),
            overrides,
            ParseContracts(provider.ContractsJson),
            provider.Description);
    }

    private static IReadOnlyList<string> ParseContracts(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static IReadOnlyList<SettingDescriptor> ParseDescriptors(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<SettingDescriptor>>(json) ?? [];

    private static IReadOnlyList<SettingDescriptorOverride> ParseOverrides(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<SettingDescriptorOverride>>(json) ?? [];
}

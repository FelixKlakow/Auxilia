using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;

namespace Auxilia.Core.Api.Services;

/// <summary>Admin curation for one manifest-declared setting: presentation overrides plus <see cref="Disabled"/> — keys and kinds stay manifest-owned.</summary>
internal sealed record SettingDescriptorOverride(
    string Key,
    string? Label = null,
    string? HelpText = null,
    string? DefaultValue = null,
    bool Disabled = false);

/// <summary>
/// Core-owned governance of the slot-provider catalog: which registered providers users may pick
/// when configuring workflows (deny-by-default availability), the slot-kind category they are
/// offered under, and presentation overrides for their manifest-declared setting descriptors.
/// The manifest stays the source of truth for setting keys and kinds. Every mutation is audited.
/// Migrated from the BackendService dashboard — the provider catalog is a Core policy concern.
/// </summary>
public sealed class ProviderCatalogService(
    IDataAccess<SlotProviderRecord> providers,
    IDataAccess<ProviderCatalogRecord> catalog,
    AuditLog auditLog)
{
    /// <summary>All registered slot-handler plugins with their curation state, filtered and paged (dependency-library registrations are not part of the catalog).</summary>
    public async Task<PagedResult<ProviderCatalogEntry>> QueryAsync(ProviderCatalogQuery query, CancellationToken ct)
    {
        var handlerRecords = (await providers.ReadAsync(ct)).Where(IsHandler).ToList();

        var entries = new List<ProviderCatalogEntry>();
        foreach (var record in handlerRecords)
            entries.Add(ToEntry(record, await catalog.ReadAsync(ProviderCatalogRecord.IdFor(record.ProviderType), ct)));

        var filtered = entries.AsEnumerable();
        if (query.Available is { } available)
            filtered = filtered.Where(e => e.Available == available);
        var ordered = filtered.OrderBy(e => e.ProviderType, StringComparer.Ordinal).ToList();
        var take = query.Take <= 0 ? 50 : query.Take;
        var page = ordered.Skip(query.Skip).Take(take).ToList();
        return new PagedResult<ProviderCatalogEntry>(page, ordered.Count, query.Skip, take);
    }

    /// <summary>Enables or disables a provider (deny-by-default): only available providers are offered in configuration editors. Audited.</summary>
    public async Task<ProviderCatalogEntry> SetAvailabilityAsync(
        string actor, string providerType, bool available, CancellationToken ct)
    {
        var (provider, curation) = await RequireProviderAsync(providerType, ct);
        var updated = curation with { Available = available };
        await catalog.SaveAsync(updated, ct);
        await auditLog.AppendAsync(actor, "provider-catalog.availability-changed",
            providerType, available ? "available" : "unavailable", ct: ct);
        return ToEntry(provider, updated);
    }

    /// <summary>
    /// Disables (or re-enables) one manifest-declared setting of a provider: a disabled setting
    /// disappears from every editor and is no longer required, so an administrator can, for
    /// example, forbid entering an external API key and force preassigned credentials. Stored
    /// values are untouched. Audited.
    /// </summary>
    public async Task<ProviderCatalogEntry> SetSettingDisabledAsync(
        string actor, string providerType, string settingKey, bool disabled, CancellationToken ct)
    {
        var (provider, curation) = await RequireProviderAsync(providerType, ct);
        var descriptors = ParseDescriptors(provider.SettingDescriptorsJson);
        if (descriptors.All(d => d.Key != settingKey))
            throw new InvalidOperationException($"'{providerType}' declares no setting '{settingKey}'.");

        var overrides = ParseOverrides(curation.DescriptorOverridesJson).ToList();
        var existing = overrides.FirstOrDefault(o => o.Key == settingKey);
        if (existing is not null)
            overrides.Remove(existing);
        overrides.Add((existing ?? new SettingDescriptorOverride(settingKey)) with { Disabled = disabled });

        var updated = curation with { DescriptorOverridesJson = JsonSerializer.Serialize(overrides) };
        await catalog.SaveAsync(updated, ct);
        await auditLog.AppendAsync(actor, "provider-catalog.setting-changed",
            providerType, $"{settingKey}: {(disabled ? "disabled" : "enabled")}", ct: ct);
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
            throw new KeyNotFoundException($"'{providerType}' is not a registered slot-handler plugin.");

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
        var disabledKeys = overrides.Where(o => o.Disabled).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var descriptors = Merge(ParseDescriptors(provider.SettingDescriptorsJson), overrides)
            .Select(d => new ProviderSettingDescriptor(
                d.Key, d.Label, d.Kind.ToString(), d.Required, d.HelpText, d.DefaultValue, d.Choices,
                disabledKeys.Contains(d.Key)))
            .ToList();
        return new ProviderCatalogEntry(
            provider.ProviderType, curation.Available, category,
            descriptors, ParseContracts(provider.ContractsJson), provider.Description);
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

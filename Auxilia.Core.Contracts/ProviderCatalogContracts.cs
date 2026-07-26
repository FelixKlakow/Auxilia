namespace Auxilia.Core.Contracts;

/// <summary>
/// One slot-provider setting as curated for editors: the plugin-manifest descriptor with any
/// admin presentation overrides merged in. <see cref="Kind"/>, <see cref="Key"/>, <see cref="Required"/>,
/// and <see cref="Choices"/> stay manifest-owned; <see cref="Disabled"/> is admin curation.
/// </summary>
public sealed record ProviderSettingDescriptor(
    string Key,
    string Label,
    string Kind,
    bool Required,
    string? HelpText,
    string? DefaultValue,
    IReadOnlyList<string>? Choices,
    bool Disabled);

/// <summary>
/// One entry of the Core-owned slot-provider catalog: a registered slot-handler provider joined
/// with its admin curation. <see cref="Available"/> is deny-by-default — a provider is offered to
/// configuration editors only after an administrator enables it here.
/// </summary>
public sealed record ProviderCatalogEntry(
    string ProviderType,
    bool Available,
    string Category,
    IReadOnlyList<ProviderSettingDescriptor> Descriptors,
    IReadOnlyList<string> Contracts,
    string? Description);

/// <summary>Set (or clear) a provider's deny-by-default availability.</summary>
public sealed record SetProviderAvailability(bool Available);

/// <summary>Disable (or re-enable) one manifest-declared setting of a provider in every editor.</summary>
public sealed record SetProviderSetting(string SettingKey, bool Disabled);

/// <summary>Filter for querying the provider catalog; <see cref="Available"/> unset returns every provider.</summary>
public sealed record ProviderCatalogQuery(
    bool? Available = null,
    int Skip = 0,
    int Take = 50);

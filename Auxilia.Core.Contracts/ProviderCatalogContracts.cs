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
    bool Disabled,
    string? ConnectFlow = null);

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

/// <summary>
/// Registers (or updates) a slot provider's descriptor in the Core catalog — normally mirrored
/// from a plugin manifest by deployment tooling; also the API a runner or operator uses to make
/// a provider configurable. Registration does NOT make it available (deny-by-default curation).
/// </summary>
public sealed record RegisterSlotProvider(
    string ProviderType,
    string Category,
    string? Description,
    IReadOnlyList<string> Contracts,
    IReadOnlyList<RegisterProviderSetting> Settings);

/// <summary>One manifest setting of a provider being registered.</summary>
public sealed record RegisterProviderSetting(
    string Key,
    string Label,
    string Kind,
    bool Required = false,
    string? HelpText = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? Choices = null,
    string? ConnectFlow = null);

/// <summary>Set (or clear) a provider's deny-by-default availability.</summary>
public sealed record SetProviderAvailability(bool Available);

/// <summary>Disable (or re-enable) one manifest-declared setting of a provider in every editor.</summary>
public sealed record SetProviderSetting(string SettingKey, bool Disabled);

/// <summary>Filter for querying the provider catalog; <see cref="Available"/> unset returns every provider.</summary>
public sealed record ProviderCatalogQuery(
    bool? Available = null,
    int Skip = 0,
    int Take = 50);

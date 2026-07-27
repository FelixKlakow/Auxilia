namespace Auxilia.Core.Contracts;

/// <summary>
/// One slot-provider setting as curated for editors: the plugin-manifest descriptor with any
/// admin presentation overrides merged in. <see cref="Kind"/>, <see cref="Key"/>, <see cref="Required"/>,
/// and <see cref="Choices"/> stay manifest-owned; <see cref="Disabled"/> is admin curation.
/// <para>
/// <see cref="Role"/> is an OPAQUE tag: the vocabulary belongs to whatever consumes the setting
/// (a runner subsystem, a plugin) — the Core and its clients only carry it. <see cref="Browse"/>
/// names a connector-browse kind that live-lists values for this setting in editors, with
/// <see cref="BrowseDependsOn"/> pointing at the sibling setting whose value scopes the browse.
/// </para>
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
    string? ConnectFlow = null,
    string? Role = null,
    string? Browse = null,
    string? BrowseDependsOn = null);

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
    string? Description,
    string? RequiredCredentialContract = null,
    bool MountsIntoWorkspace = false,
    bool ComposesEnvironment = false,
    ProviderOAuthRefresh? OAuthRefresh = null);

/// <summary>
/// Declares, as pure data, how a provider's stored OAuth credential is refreshed: the Core
/// exchanges the refresh token at the endpoint whenever the access token is (or may be) stale,
/// persists the rotated values, and delivers only fresh tokens. The KEYS name the provider's own
/// settings — the Core stays provider-agnostic.
/// </summary>
public sealed record ProviderOAuthRefresh(
    string TokenEndpoint,
    string ClientId,
    string AccessTokenKey,
    string RefreshTokenKey,
    string ExpiresAtKey);

/// <summary>
/// Declares, as pure data, how the provider's MODEL catalog is listed live: the endpoint, how
/// the connector's credential authenticates the call (an API-key header and/or a bearer
/// setting), and where ids/labels sit in the response. The Core executes this generically —
/// like <see cref="ProviderOAuthRefresh"/>, the vendor knowledge lives in the registration.
/// </summary>
public sealed record ProviderModelCatalog(
    string Endpoint,
    string ItemsPath,
    string IdField,
    string? LabelField = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? ApiKeyHeader = null,
    string? ApiKeySettingKey = null,
    string? BearerSettingKey = null,
    IReadOnlyDictionary<string, string>? BearerHeaders = null);

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
    IReadOnlyList<RegisterProviderSetting> Settings,
    string? RequiredCredentialContract = null,
    bool MountsIntoWorkspace = false,
    bool ComposesEnvironment = false,
    ProviderOAuthRefresh? OAuthRefresh = null,
    ProviderModelCatalog? ModelCatalog = null);

/// <summary>One manifest setting of a provider being registered.</summary>
public sealed record RegisterProviderSetting(
    string Key,
    string Label,
    string Kind,
    bool Required = false,
    string? HelpText = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? Choices = null,
    string? ConnectFlow = null,
    string? Role = null,
    string? Browse = null,
    string? BrowseDependsOn = null);

/// <summary>Set (or clear) a provider's deny-by-default availability.</summary>
public sealed record SetProviderAvailability(bool Available);

/// <summary>Disable (or re-enable) one manifest-declared setting of a provider in every editor.</summary>
public sealed record SetProviderSetting(string SettingKey, bool Disabled);

/// <summary>Filter for querying the provider catalog; <see cref="Available"/> unset returns every provider.</summary>
public sealed record ProviderCatalogQuery(
    bool? Available = null,
    int Skip = 0,
    int Take = 50);

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
    ProviderOAuthRefresh? OAuthRefresh = null,
    IReadOnlyList<EnvironmentBaseRef>? EnvironmentBases = null)
{
    /// <summary>
    /// Who may bind this entry into a run (dispatch-enforced); empty follows the platform's
    /// <c>security.default-resource-access</c> setting (restricted = administrators only).
    /// </summary>
    public IReadOnlyList<AccessGrant> Grants { get; init; } = [];

    /// <summary>
    /// Tool names this provider needs inside the run container (manifest-declared, e.g. the CLI
    /// it drives) — editors offer and the Core admits the provider only for workflows whose
    /// schema provides every listed tool. Empty = no in-image requirement.
    /// </summary>
    public IReadOnlyList<string> RequiredTools { get; init; } = [];
}

/// <summary>
/// Restricts who may bind a catalog entry (slot provider or environment layer) into a run:
/// empty grants defer to the platform default-access setting; non-empty grants admit only the
/// listed subjects at dispatch. Administrators always pass.
/// </summary>
public sealed record SetProviderGrants(IReadOnlyList<AccessGrant> Grants);

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
/// Declares, as pure data, ONE live-browse kind of a provider (e.g. <c>"repositories"</c>,
/// <c>"branches"</c>): the request template, how the connector's credential authenticates the
/// call (bearer or PAT basic auth — both named by SETTING KEY), and where item ids/labels sit
/// in the response. The Core executes this generically — like <see cref="ProviderModelCatalog"/>
/// and <see cref="ProviderOAuthRefresh"/>, the vendor knowledge lives in the registration.
/// <para>
/// <c>{placeholders}</c> in URL templates resolve to: <c>{context}</c> — the browse request's
/// context verbatim; <c>{context:owner-repo}</c> — the context's last two URL path segments with
/// a trailing <c>.git</c> stripped; <c>{resolved.&lt;path&gt;}</c> — a field of the item the
/// <see cref="Resolve"/> pre-step matched; any other name — the connector setting of that key,
/// trailing '/' trimmed. Field paths (<see cref="IdField"/> etc.) dot-traverse nested objects.
/// </para>
/// </summary>
public sealed record ProviderBrowseSpec(
    string UrlTemplate,
    string IdField,
    string? ItemsPath = null,
    string? LabelField = null,
    string? LabelPrefixField = null,
    string? IdTrimPrefix = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? BearerSettingKey = null,
    string? BasicPasswordSettingKey = null,
    bool SortByLabel = false,
    ProviderBrowseResolve? Resolve = null);

/// <summary>
/// Optional pre-step of a <see cref="ProviderBrowseSpec"/>: list candidate items, match the
/// browse CONTEXT against them (normalized-URL match on <see cref="MatchUrlField"/> first, then
/// the context's last path segment against <see cref="MatchNameField"/>), and expose the matched
/// item's <see cref="ExportFields"/> as <c>{resolved.&lt;path&gt;}</c> placeholders to the main
/// request — e.g. resolving a clone URL to a repository id before listing its branches.
/// </summary>
public sealed record ProviderBrowseResolve(
    string UrlTemplate,
    string ItemsPath,
    string MatchUrlField,
    string MatchNameField,
    IReadOnlyList<string> ExportFields);

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
    ProviderModelCatalog? ModelCatalog = null,
    IReadOnlyList<EnvironmentBaseRef>? EnvironmentBases = null,
    IReadOnlyList<string>? RequiredTools = null,
    IReadOnlyDictionary<string, ProviderBrowseSpec>? BrowseSpecs = null);

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

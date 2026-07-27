namespace Auxilia.Core.Contracts;

/// <summary>
/// A workflow slot bound the ONE generic way every binding works: to a stored connector (by id —
/// secrets stay in the Core), to a provider type with inline non-secret settings, to BOTH (the
/// settings parameterize the binding, the connector supplies its credential — e.g. a workspace
/// mount whose provider declares a required credential contract), or — when
/// <see cref="DelegatedResource"/> is set — to a token obtained on-behalf-of the triggering user
/// at resolution time (OBO delegation). A slot declaring <c>AllowMultiple</c> may appear in
/// several bindings of one run or configuration.
/// </summary>
public sealed record SlotBinding(
    string SlotName,
    string? ProviderType = null,
    Guid? ConnectorId = null,
    IReadOnlyDictionary<string, string>? Settings = null,
    string? DelegatedResource = null);

/// <summary>
/// Create a run configuration in the Core store. The configuration references a registered
/// workflow type; the package coordinate is resolved from the workflow-type registry at dispatch.
/// </summary>
public sealed record CreateRunConfiguration(
    string Name,
    string WorkflowType,
    IReadOnlyDictionary<string, string>? Context = null,
    IReadOnlyList<SlotBinding>? SlotBindings = null,
    bool Enabled = true,
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// Update a stored run configuration: a null field stays unchanged, a provided one REPLACES the
/// stored value wholesale (context, bindings, and tags are replaced as complete sets — editors
/// send the full edited state, not deltas). The workflow type is immutable.
/// </summary>
public sealed record UpdateRunConfiguration(
    string? Name = null,
    IReadOnlyDictionary<string, string>? Context = null,
    IReadOnlyList<SlotBinding>? SlotBindings = null,
    bool? Enabled = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>A stored run configuration the Core resolves into a run spec on dispatch.</summary>
public sealed record RunConfiguration(
    Guid Id,
    string Name,
    string WorkflowType,
    IReadOnlyDictionary<string, string> Context,
    IReadOnlyList<SlotBinding> SlotBindings,
    bool Enabled,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<string>? Tags = null);

/// <summary>Filter for querying configurations.</summary>
public sealed record ConfigurationQuery(
    string? WorkflowType = null,
    bool? Enabled = null,
    int Skip = 0,
    int Take = 50);

namespace Auxilia.Core.Contracts;

/// <summary>
/// A workflow slot bound to a stored connector (by id — secrets stay in the Core), to a raw
/// provider type with inline settings, or — when <see cref="DelegatedResource"/> is set — to a
/// token obtained on-behalf-of the triggering user at resolution time (OBO delegation).
/// Connector-backed and delegated bindings carry no settings of their own.
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

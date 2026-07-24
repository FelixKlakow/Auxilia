namespace Auxilia.Core.Contracts;

/// <summary>
/// A workflow slot bound either to a stored connector (by id — secrets stay in the Core) or
/// to a raw provider type with inline settings. Connector-backed bindings carry no settings
/// of their own.
/// </summary>
public sealed record SlotBinding(
    string SlotName,
    string? ProviderType = null,
    Guid? ConnectorId = null,
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>Create a run configuration in the Core store.</summary>
public sealed record CreateRunConfiguration(
    string Name,
    string WorkflowType,
    string PackageUri,
    IReadOnlyDictionary<string, string>? Context = null,
    IReadOnlyList<SlotBinding>? SlotBindings = null,
    bool Enabled = true);

/// <summary>A stored run configuration the Core resolves into a run spec on dispatch.</summary>
public sealed record RunConfiguration(
    Guid Id,
    string Name,
    string WorkflowType,
    string PackageUri,
    IReadOnlyDictionary<string, string> Context,
    IReadOnlyList<SlotBinding> SlotBindings,
    bool Enabled,
    DateTimeOffset UpdatedUtc);

/// <summary>Filter for querying configurations.</summary>
public sealed record ConfigurationQuery(
    string? WorkflowType = null,
    bool? Enabled = null,
    int Skip = 0,
    int Take = 50);

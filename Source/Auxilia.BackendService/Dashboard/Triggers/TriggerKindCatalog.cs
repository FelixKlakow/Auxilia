using Auxilia.PlatformData.Entities;
using Auxilia.Workflows;

namespace Auxilia.BackendService.Dashboard.Triggers;

/// <summary>
/// Describes one trigger kind to the configuration UI: how it is labeled and which settings
/// it needs. Forms render generically from <see cref="Settings"/> — no per-kind UI code.
/// </summary>
public sealed record TriggerKindDescriptor(
    string Kind,
    string Label,
    string Description,
    IReadOnlyList<SettingDescriptor> Settings,
    bool Wireable = true);

/// <summary>One trigger being edited; its settings are keyed by the kind's descriptor.</summary>
public sealed class TriggerDraft
{
    /// <summary>The stored record this draft edits; null when newly added.</summary>
    public Guid? ExistingId { get; set; }

    public string Kind { get; set; } = "";

    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);
}

/// <summary>What a binding needs to validate a draft beyond its own settings.</summary>
public sealed record TriggerValidationContext(
    IReadOnlyDictionary<Guid, SlotInstanceRecord> InstancesById,
    Guid? ActorPrincipalId);

/// <summary>One wired trigger of a configuration, ready for the list and flow surfaces.</summary>
public sealed record ConfiguredTrigger(
    Guid ConfigurationId,
    string Kind,
    string ListLabel,
    string FlowLabel,
    string? Health,
    bool Failing);

/// <summary>
/// Storage adapter of one trigger kind. The platform's trigger vocabulary is whatever set of
/// bindings is registered at runtime — the editor, validation, and persistence code never
/// enumerate kinds themselves, so a new kind ships as one additional registration (e.g. from
/// a plugin) without recompiling anything that exists.
/// </summary>
public interface ITriggerKindBinding
{
    TriggerKindDescriptor Descriptor { get; }

    /// <summary>Kind-specific validation beyond the generic required-setting check.</summary>
    IEnumerable<string> Validate(TriggerDraft trigger, TriggerValidationContext context);

    Task<IReadOnlyList<TriggerDraft>> LoadAsync(Guid configurationId, CancellationToken ct);

    /// <summary>Creates or updates the record of one draft; audited.</summary>
    Task SaveAsync(string actor, Guid? actorPrincipalId, WorkflowConfigurationDraft draft,
        Guid configurationId, TriggerDraft trigger, CancellationToken ct);

    /// <summary>Deletes this kind's records of the configuration except the kept ones; audited.</summary>
    Task RemoveExceptAsync(string actor, Guid configurationId, IReadOnlySet<Guid> keptIds, CancellationToken ct);

    Task SetEnabledAsync(Guid configurationId, bool enabled, CancellationToken ct);

    /// <summary>Every wired trigger of this kind across all configurations, with live health.</summary>
    Task<IReadOnlyList<ConfiguredTrigger>> ListConfiguredAsync(CancellationToken ct);
}

/// <summary>
/// The trigger kinds this platform instance knows, assembled at runtime from the registered
/// <see cref="ITriggerKindBinding"/>s plus the wiring-free "manual" kind.
/// </summary>
public sealed class TriggerKindCatalog(IEnumerable<ITriggerKindBinding> bindings)
{
    /// <summary>Manual runs need no wiring — the kind only marks a workflow as manually startable.</summary>
    public static readonly TriggerKindDescriptor Manual = new(
        TriggerDeclaration.Manual, "Manual",
        "Started by a person from the dashboard or workflows page with an instruction.",
        [], Wireable: false);

    private readonly IReadOnlyList<ITriggerKindBinding> _bindings = bindings.ToList();

    public IReadOnlyList<TriggerKindDescriptor> All
        => [Manual, .. _bindings.Select(b => b.Descriptor)];

    public IReadOnlyList<ITriggerKindBinding> Bindings => _bindings;

    public TriggerKindDescriptor? Find(string kind)
        => All.FirstOrDefault(d => d.Kind == kind);

    public ITriggerKindBinding? BindingOf(string kind)
        => _bindings.FirstOrDefault(b => b.Descriptor.Kind == kind);
}

namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Seeds (creates or updates) a named workflow configuration on the Steering Instance via the
/// <c>slot-configurations</c> exchange or the per-instance <c>*-slot-seed.upsert-configuration</c>
/// queue — the same seeding family as slot configurations.
/// </summary>
public sealed record UpsertWorkflowConfigurationCommand(
    /// <summary>Natural key; the stored record ID is derived deterministically from it.</summary>
    string Name,
    string DisplayName,
    string WorkflowType,
    string PackageUri,
    bool Enabled,
    IReadOnlyList<SlotBindingSeed> SlotBindings,
    Guid? OwnerPrincipalId = null);

/// <summary>
/// One slot binding of an <see cref="UpsertWorkflowConfigurationCommand"/>. Either inline
/// (provider type + settings) or referencing a reusable slot instance by ID — then provider
/// and settings are resolved from the instance and the inline values are ignored.
/// </summary>
public sealed record SlotBindingSeed(
    string SlotName,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings,
    Guid? SlotInstanceId = null);

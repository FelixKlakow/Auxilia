using Auxilia.PlatformData.Entities;

namespace Auxilia.SteeringInstance.Workflows.Storage;

/// <summary>
/// Decrypted in-memory view of a named workflow configuration (#18): one workflow type can be
/// configured any number of times; each configuration binds the type's slots to providers and
/// settings and carries the package URI to dispatch.
/// </summary>
public sealed record StoredWorkflowConfiguration(
    string Name,
    string DisplayName,
    string WorkflowType,
    string PackageUri,
    bool Enabled,
    IReadOnlyList<StoredSlotBinding> SlotBindings,
    Guid? OwnerPrincipalId = null)
{
    /// <summary>Deterministic record ID derived from the natural-key name.</summary>
    public Guid Id => WorkflowConfigurationRecord.IdFor(Name);

    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>One slot binding of a <see cref="StoredWorkflowConfiguration"/>.</summary>
public sealed record StoredSlotBinding(
    string SlotName,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings);

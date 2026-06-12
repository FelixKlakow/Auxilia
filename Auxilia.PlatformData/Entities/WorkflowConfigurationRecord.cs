using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Named workflow configuration: one workflow type can be configured any number of times
/// (different sources/providers per instance). <see cref="SlotBindingsJson"/> is the serialized
/// list of <see cref="WorkflowConfigurationSlotBinding"/> entries whose settings are run through
/// <see cref="Protection.ISettingsProtector"/> per binding — never persisted in the clear when a
/// key is configured.
/// </summary>
public sealed record WorkflowConfigurationRecord : IEntity
{
    public Guid Id { get; init; }
    /// <summary>Natural key; the record ID is derived deterministically from it.</summary>
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string WorkflowType { get; init; }
    public required string PackageUri { get; init; }
    public bool Enabled { get; init; }
    public Guid? OwnerPrincipalId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public required string SlotBindingsJson { get; init; }

    public static Guid IdFor(string name) => DeterministicGuid.For("workflow-configuration", name);
}

/// <summary>One slot binding inside <see cref="WorkflowConfigurationRecord.SlotBindingsJson"/>.</summary>
public sealed record WorkflowConfigurationSlotBinding
{
    public required string SlotName { get; init; }
    public required string ProviderType { get; init; }
    /// <summary>The binding's settings dictionary as JSON, protected via <see cref="Protection.ISettingsProtector"/>.</summary>
    public required string ProtectedSettingsJson { get; init; }
}

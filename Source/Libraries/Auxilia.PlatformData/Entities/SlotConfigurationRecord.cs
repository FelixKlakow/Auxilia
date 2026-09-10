using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Stored slot configuration; one record per (workflow type, slot name).
/// <see cref="ProtectedSettingsJson"/> is the JSON settings dictionary run through
/// <see cref="Protection.ISettingsProtector"/> — never persisted in the clear when a key is configured.
/// </summary>
public sealed record SlotConfigurationRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public required string SlotName { get; init; }
    public required string ProviderType { get; init; }
    public required string ProtectedSettingsJson { get; init; }
    public required string Status { get; init; }

    public static Guid IdFor(string workflowType, string slotName)
        => DeterministicGuid.For("slot-configuration", workflowType, slotName);
}

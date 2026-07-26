using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>Registered slot-handler plugin; one record per provider type.</summary>
public sealed record SlotProviderRecord : IEntity
{
    public Guid Id { get; init; }
    public required string ProviderType { get; init; }
    public required string DllPath { get; init; }
    /// <summary>Serialized setting-descriptor list from the plugin manifest; null when the provider ships none.</summary>
    public string? SettingDescriptorsJson { get; init; }
    /// <summary>Serialized list of capability contract full names from the manifest; null = unclassified.</summary>
    public string? ContractsJson { get; init; }
    /// <summary>Manifest-declared slot-kind tag; the provider catalog may override it.</summary>
    public string? Category { get; init; }

    /// <summary>Contract a credential connector must implement to authenticate this provider's bindings.</summary>
    public string? RequiredCredentialContract { get; init; }

    /// <summary>Bindings of this provider are materialized into the run's workspace before launch.</summary>
    public bool MountsIntoWorkspace { get; init; }

    /// <summary>Manifest-declared plain-language description of what the provider does.</summary>
    public string? Description { get; init; }

    public static Guid IdFor(string providerType) => DeterministicGuid.For("slot-provider", providerType);
}

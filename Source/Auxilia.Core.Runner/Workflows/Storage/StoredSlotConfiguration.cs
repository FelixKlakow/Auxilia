namespace Auxilia.Core.Runner.Workflows.Storage;

public sealed record StoredSlotConfiguration(
    string SlotName,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings,
    ConfigurationStatus Status);

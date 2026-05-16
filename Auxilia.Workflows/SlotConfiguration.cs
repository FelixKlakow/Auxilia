/// <summary>
/// Decrypted configuration for a single slot.
/// Intended exclusively for <see cref="ISlotHandler"/> implementations in slot packages.
/// Workflow application code must not read or store these values directly.
/// </summary>
public record SlotConfiguration(
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings);

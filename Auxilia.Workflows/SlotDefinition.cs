namespace Auxilia.Workflows;

public sealed record SlotDefinition(
    string SlotName,
    object? Capabilities,
    string? Description = null);

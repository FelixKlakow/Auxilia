namespace Auxilia.Workflows;

public sealed record SlotDefinition(
    string SlotName,
    object? Capabilities,
    string? Description = null)
{
    public required Type ServiceType { get; init; }
}

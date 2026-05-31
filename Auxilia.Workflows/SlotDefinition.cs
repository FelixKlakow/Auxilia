using System.Text.Json.Serialization;

namespace Auxilia.Workflows;

public sealed record SlotDefinition(
    string SlotName,
    object? Capabilities,
    string? Description = null)
{
    /// <summary>
    /// Runtime DI service type. Not persisted to JSON — populated in-process only.
    /// </summary>
    [JsonIgnore]
    public Type? ServiceType { get; init; }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.AiAgent;

public record AiCapabilities : ICapability
{
    public required int MinContextWindow { get; init; }
    public required Modality[] SupportedModalities { get; init; }
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Opaque forward-compatibility passthrough.
    /// Captures unknown JSON properties so they survive a serialize-deserialize round-trip
    /// when the schema gains new fields. Must never be read for capability or permission logic.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.AiAgent;

public record AiCapabilities : ICapability
{
    public required int MinContextWindow { get; init; }
    public required Modality[] SupportedModalities { get; init; }
    public int? MaxOutputTokens { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

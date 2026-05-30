using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.TestRunner;

public record TestRunnerCapabilities : ICapability
{
    /// <summary>
    /// Opaque forward-compatibility passthrough.
    /// Captures unknown JSON properties so they survive a serialize-deserialize round-trip
    /// when the schema gains new fields. Must never be read for capability or permission logic.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

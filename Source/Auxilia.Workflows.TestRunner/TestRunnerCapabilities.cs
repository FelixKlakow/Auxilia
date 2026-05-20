using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.TestRunner;

public record TestRunnerCapabilities : ICapability
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

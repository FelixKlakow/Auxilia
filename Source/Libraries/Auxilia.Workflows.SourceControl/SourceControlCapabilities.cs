using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Workflows;
using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.SourceControl;

public record SourceControlCapabilities : ICapability
{
    public required Permission[] RequiredPermissions { get; init; }

    /// <summary>
    /// Open host-type vocabulary (see <see cref="SourceHostTypes"/>); compare OrdinalIgnoreCase
    /// and tolerate unknown values.
    /// </summary>
    public string[]? SupportedHostTypes { get; init; }

    /// <summary>
    /// Opaque forward-compatibility passthrough.
    /// Captures unknown JSON properties so they survive a serialize-deserialize round-trip
    /// when the schema gains new fields. Must never be read for capability or permission logic.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

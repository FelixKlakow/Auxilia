using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.PullRequestAccess;

public record PullRequestAccessCapabilities : ICapability
{
    public required string[] RequiredPermissions { get; init; }
    public string? PrHostType { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows.Testing;

/// <summary>
/// Minimal credentialed slot contract for the Core credential-resolution system test: exposes
/// the decrypted slot settings that were resolved just-in-time through the Core.
/// </summary>
public interface ICredentialProbe
{
    string? GetSetting(string key);
}

/// <summary>Capabilities for an <see cref="ICredentialProbe"/> slot (no specific requirements).</summary>
public record CredentialProbeCapabilities : ICapability
{
    /// <summary>Opaque forward-compatibility passthrough (see other capability records).</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public static class CredentialProbeWorkflowBuilderExtensions
{
    /// <summary>Declares an <see cref="ICredentialProbe"/> slot backed by a Core-resolved connector.</summary>
    public static IWorkflowBuilder RequiresCredentialProbe(
        this IWorkflowBuilder builder,
        string name,
        CredentialProbeCapabilities capabilities,
        string? description = null)
        => builder.Requires<ICredentialProbe>(name, capabilities, description);
}

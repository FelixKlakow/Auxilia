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

    /// <summary>
    /// Full name of the capability contract this slot expects (e.g.
    /// <c>Auxilia.Workflows.TaskSource.ITaskSourceAccess</c>). Persisted so the platform can
    /// offer only providers implementing the matching contract.
    /// </summary>
    public string? Contract { get; init; }

    /// <summary>An optional slot may stay unbound in a workflow configuration.</summary>
    public bool Optional { get; init; }

    /// <summary>The slot accepts several bindings in one configuration or run (e.g. many repositories).</summary>
    public bool AllowMultiple { get; init; }
}

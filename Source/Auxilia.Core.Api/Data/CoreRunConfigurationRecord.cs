using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// A stored run configuration owned by the Core. The Core resolves it into a self-contained
/// run spec at dispatch — the runner never reads this store (Principle 4: isolated Core DB).
/// </summary>
public sealed record CoreRunConfigurationRecord : IEntity
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string WorkflowType { get; init; }

    /// <summary>Non-secret key/value run context, serialized as JSON.</summary>
    public string ContextJson { get; init; } = "{}";

    /// <summary>Slot bindings (connector references / inline provider settings), serialized as JSON.</summary>
    public string SlotBindingsJson { get; init; } = "[]";

    /// <summary>Free-form tags for steering client filtering, serialized as JSON.</summary>
    public string TagsJson { get; init; } = "[]";

    public bool Enabled { get; init; } = true;
    public DateTimeOffset UpdatedUtc { get; init; }
}

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

    /// <summary>"Company" (shared) or "Personal" (owner + granted subjects + managers only).</summary>
    public string Scope { get; init; } = Core.Contracts.ResourceScope.Company;

    /// <summary>The principal who owns a personal configuration; null for company configurations.</summary>
    public Guid? OwnerPrincipalId { get; init; }

    /// <summary>JSON array of <c>AccessGrant</c> admitting subjects to a personal configuration.</summary>
    public string GrantsJson { get; init; } = "[]";

    public bool Enabled { get; init; } = true;
    public DateTimeOffset UpdatedUtc { get; init; }
}

using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// A signed-in external account ("my Claude account", "the company GitHub bot"): the
/// credential a connect flow obtained, stored once and picked from wherever a slot setting
/// names that flow. The token is protected via <see cref="Protection.ISettingsProtector"/>
/// and write-only after creation.
/// </summary>
public sealed record ConnectorRecord : IEntity
{
    public Guid Id { get; init; }
    /// <summary>Natural key; the record ID is derived deterministically from it.</summary>
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Key of the connect flow that produced the credential (e.g. "anthropic-claude").</summary>
    public required string FlowKey { get; init; }
    public required string ProtectedToken { get; init; }
    /// <summary>"Company" = usable by everyone configuring slots; "Personal" = owner only.</summary>
    public string Scope { get; init; } = SlotInstanceScope.Company;
    public Guid? OwnerPrincipalId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    public static Guid IdFor(string name) => DeterministicGuid.For("connector", name);
}

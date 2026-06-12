using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// A principal's composed dashboard (ARCHITECTURE §15): the ordered list of views pinned
/// from any number of runs, stored as JSON. One record per principal.
/// </summary>
public sealed record DashboardRecord : IEntity
{
    public Guid Id { get; init; }
    public Guid PrincipalId { get; init; }
    /// <summary>Ordered JSON array of pinned views (instance id, view name, display title).</summary>
    public required string PinsJson { get; init; }

    public static Guid IdFor(Guid principalId) => DeterministicGuid.For("dashboard", principalId.ToString("D"));
}

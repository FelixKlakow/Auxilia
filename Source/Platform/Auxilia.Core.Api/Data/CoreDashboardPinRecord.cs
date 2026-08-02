using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// One run view a principal pinned to their dashboard. Pins are personal — every read and
/// delete is scoped to the owning principal.
/// </summary>
public sealed record CoreDashboardPinRecord : IEntity
{
    public Guid Id { get; init; }

    public Guid PrincipalId { get; init; }

    public Guid RunId { get; init; }

    public required string ViewName { get; init; }

    /// <summary>Denormalized at pin time so the dashboard can label the card without a run read.</summary>
    public required string WorkflowType { get; init; }

    public DateTimeOffset PinnedUtc { get; init; }

    /// <summary>Deterministic per (principal, run, view) — pinning twice stays one pin.</summary>
    public static Guid IdFor(Guid principalId, Guid runId, string viewName)
        => DeterministicGuid.For("dashboard-pin", principalId.ToString("N"), runId.ToString("N"), viewName);
}

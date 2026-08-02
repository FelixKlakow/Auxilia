using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// One persisted view item (ARCHITECTURE §15): finished runs replay their views from these
/// records through the identical rendering path used for live data.
/// </summary>
public sealed record ViewDataRecord : IEntity
{
    public Guid Id { get; init; }
    public Guid WorkflowInstanceId { get; init; }
    public required string ViewName { get; init; }
    public long Sequence { get; init; }
    public required string PayloadJson { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }

    public static Guid IdFor(Guid instanceId, string viewName, long sequence)
        => DeterministicGuid.For("view-data", instanceId.ToString("D"), "", viewName, "", sequence.ToString());
}

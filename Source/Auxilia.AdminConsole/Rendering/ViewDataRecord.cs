namespace Auxilia.AdminConsole.Rendering;

/// <summary>
/// One view item as consumed by <c>ViewRenderer</c>/<c>AgentChatRenderer</c>. A DB-free DTO local to
/// the console (the platform's persisted <c>Auxilia.PlatformData.Entities.ViewDataRecord</c> stays in
/// the Core) — decoded from the Core's SSE <c>RunStreamEvent</c> / persisted view-read in later slices.
/// The renderers use only <see cref="Sequence"/>, <see cref="PayloadJson"/>, and <see cref="TimestampUtc"/>.
/// </summary>
public sealed record ViewDataRecord
{
    public Guid Id { get; init; }
    public Guid WorkflowInstanceId { get; init; }
    public required string ViewName { get; init; }
    public long Sequence { get; init; }
    public required string PayloadJson { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
}

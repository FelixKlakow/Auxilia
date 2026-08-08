namespace Auxilia.Core.Contracts;

/// <summary>
/// A persisted platform event as exposed on the Core client surface: a small, named fact
/// published by a workflow (via the SDK) or by the platform itself (run lifecycle). Large
/// data belongs in the Artifact Store, referenced from <see cref="PayloadJson"/>.
/// </summary>
public sealed record EventDto(
    Guid Id,
    string EventType,
    string WorkflowType,
    string WorkItemId,
    Guid? SourceRunId,
    string? PayloadJson,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Typed filter for event queries; all filters optional and combined with AND. Results are
/// newest-first, EXCEPT when <see cref="CreatedAfterUtc"/> is set — the catch-up shape pages
/// oldest-first so a reconnecting consumer drains a gap deterministically.
/// </summary>
public sealed record EventQuery(
    string? EventType = null,
    string? WorkItemId = null,
    Guid? SourceRunId = null,
    DateTimeOffset? CreatedAfterUtc = null,
    /// <summary>Browse-shaped upper bound: only events strictly before this instant, ordering unchanged.</summary>
    DateTimeOffset? CreatedBeforeUtc = null,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// One frame of the event SSE stream (<c>GET /api/events/stream</c>). The stream is
/// server-side filtered — a subscriber receives only the event types / work items it asked
/// for. Bus re-delivery can duplicate a frame; consumers that care de-duplicate by
/// <see cref="EventDto.Id"/>.
/// </summary>
public sealed record EventStreamEvent(
    EventDto Event,
    DateTimeOffset TimestampUtc);

/// <summary>
/// The reserved platform-published event vocabulary (run lifecycle). Workflow-published
/// events may not use the reserved prefix; the vocabulary is otherwise open — event types
/// are free-form strings declared per workflow, never a compiled enum.
/// </summary>
public static class PlatformEventTypes
{
    public const string ReservedPrefix = "run.";
    public const string RunSucceeded = "run.succeeded";
    public const string RunFailed = "run.failed";
    public const string RunCancelled = "run.cancelled";
}

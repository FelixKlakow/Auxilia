namespace Auxilia.Core.Contracts;

/// <summary>
/// Start a workflow run directly, without a stored configuration — the "configured on the
/// fly" path. The run references a registered workflow type only; the Core resolves the
/// signed package coordinate from its workflow-type registry (Active types only).
/// </summary>
public sealed record RunRequest(
    string WorkflowType,
    IReadOnlyDictionary<string, string>? Context = null,
    Guid? RequestedBy = null,
    IReadOnlyList<SlotBinding>? SlotBindings = null);

/// <summary>Acknowledgement that a run was accepted and dispatched to the runner.</summary>
public sealed record RunAccepted(Guid RunId, Guid CommandId);

/// <summary>
/// An opaque input delivered into a running workflow (guidance / a decision / halt — the Core
/// carries the payload verbatim and never interprets it).
/// </summary>
public sealed record ProvideRunInput(string PayloadJson);

/// <summary>Point-in-time status of a run, resolved from the Core's run-instance store.</summary>
public sealed record RunStatus(
    Guid RunId,
    string WorkflowType,
    string State,
    string? Error,
    DateTimeOffset CreatedUtc,
    Guid? ConfigurationId,
    string? ConfigurationName,
    /// <summary>When the run reached its terminal state; null while it is still in flight.</summary>
    DateTimeOffset? CompletedUtc = null,
    /// <summary>The run hosts an interactive web terminal reachable via the terminal-ticket endpoint.</summary>
    bool HasTerminal = false)
{
    /// <summary>Wall-clock duration; null until the run completes.</summary>
    public TimeSpan? Duration => CompletedUtc - CreatedUtc;
}

/// <summary>
/// Short-lived access to a run's interactive web terminal: open <see cref="Url"/> (relative to
/// the Core base address) before <see cref="ExpiresUtc"/> — the Core proxies the terminal;
/// the workflow container itself is never reachable directly.
/// </summary>
public sealed record TerminalTicket(Guid RunId, string Url, DateTimeOffset ExpiresUtc);

/// <summary>Filter for querying runs. Unset fields are ignored; paging is always applied.</summary>
public sealed record RunQuery(
    string? State = null,
    string? WorkflowType = null,
    Guid? ConfigurationId = null,
    int Skip = 0,
    int Take = 50);

/// <summary>A page of results plus the total number of matches before paging.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);

/// <summary>
/// One persisted view item of a run — the read-later counterpart of the live stream's view
/// frames. <see cref="Sequence"/> is per-(run, view) monotonic.
/// </summary>
public sealed record RunViewItem(
    string ViewName,
    long Sequence,
    string PayloadJson,
    DateTimeOffset TimestampUtc);

/// <summary>
/// One frame of a run's live-view stream (SSE). A discriminated envelope so a client can tell a
/// lifecycle transition from a view item without a second round-trip: <see cref="Kind"/> is
/// <c>"status"</c> (the payload is a serialized <c>WorkflowStatusEvent</c>) or <c>"view"</c> (the
/// payload is a serialized <c>ViewDataMessage</c>). <see cref="Sequence"/> is the view item's
/// per-(run, view) monotonic sequence for view frames, and <c>0</c> for status frames.
/// </summary>
public sealed record RunStreamEvent(
    string Kind,
    Guid RunId,
    long Sequence,
    string PayloadJson,
    DateTimeOffset TimestampUtc)
{
    public const string StatusKind = "status";
    public const string ViewKind = "view";
}

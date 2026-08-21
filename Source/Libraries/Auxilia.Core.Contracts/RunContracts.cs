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

/// <summary>Aggregate run counts, computed server-side for dashboards (no page scraping).</summary>
public sealed record RunStats(
    int Total,
    /// <summary>Runs currently in a non-terminal state.</summary>
    int Active,
    /// <summary>Count per run state (e.g. "Success", "Failed", "Running").</summary>
    IReadOnlyDictionary<string, int> ByState);

/// <summary>One run view pinned to the caller's dashboard (pins are personal).</summary>
public sealed record DashboardPin(
    Guid Id,
    Guid RunId,
    string ViewName,
    /// <summary>Denormalized at pin time so the dashboard can label the card without a run read.</summary>
    string WorkflowType,
    DateTimeOffset PinnedUtc);

public sealed record CreateDashboardPin(Guid RunId, string ViewName);

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

    /// <summary>
    /// The typed status payload of a <see cref="StatusKind"/> frame; null for view frames or
    /// an unparseable payload. Clients switch on <see cref="RunStreamStatus.State"/> with
    /// <see cref="RunStates"/> instead of hand-parsing <see cref="PayloadJson"/>.
    /// </summary>
    public RunStreamStatus? AsStatus()
        => Kind == StatusKind ? TryDeserialize<RunStreamStatus>() : null;

    /// <summary>The typed view payload of a <see cref="ViewKind"/> frame; null otherwise.</summary>
    public RunStreamView? AsView()
        => Kind == ViewKind ? TryDeserialize<RunStreamView>() : null;

    private T? TryDeserialize<T>() where T : class
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(
                PayloadJson, System.Text.Json.JsonSerializerOptions.Web);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The typed shape of a status stream frame — the client-side mirror of the runner's wire
/// status event. <see cref="WorkflowInstanceId"/> is the runner-assigned id (equal to
/// <see cref="RunStreamEvent.RunId"/> unless the caller subscribed by the dispatch id).
/// </summary>
public sealed record RunStreamStatus(
    Guid WorkflowInstanceId,
    string WorkflowType,
    string State,
    string? ErrorMessage,
    DateTimeOffset TimestampUtc,
    Guid? OwnerServiceId = null,
    Guid? CommandId = null)
{
    public bool IsTerminal => RunStates.IsTerminal(State);
}

/// <summary>The typed shape of a view stream frame: one item of a declared live view.</summary>
public sealed record RunStreamView(
    Guid WorkflowInstanceId,
    string ViewName,
    long Sequence,
    string PayloadJson);

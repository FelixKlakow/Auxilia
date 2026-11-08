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
    IReadOnlyList<SlotBinding>? SlotBindings = null)
{
    /// <summary>
    /// Tools the run needs the workflow's image to provide (<see cref="ToolProvisions"/>:
    /// <c>name</c> or <c>name@version</c>). The runner checks them before launch: a run whose image
    /// provides another version ends <c>PreFlightFailed</c> instead of running on it.
    /// </summary>
    public IReadOnlyList<string>? RequiredTools { get; init; }

    /// <summary>
    /// Files the run receives: the name the workflow declares (<c>RequiresInputFile</c>) → the id
    /// of a file the caller uploaded (<see cref="RunInputFile"/>). Each upload binds to this one
    /// run, reaches its container at launch, and is deleted when the run ends.
    /// </summary>
    public IReadOnlyDictionary<string, Guid>? InputFiles { get; init; }

    /// <summary>
    /// Hours this run's checkpoints are kept after their latest save — overriding the workflow's
    /// and the general lifetime, where the platform lets runs do that
    /// (<see cref="PlatformSettingKeys.CheckpointLifetimeOverrides"/>); refused otherwise, and above
    /// the platform's maximum.
    /// </summary>
    public double? CheckpointLifetimeHours { get; init; }
}

/// <summary>
/// A file uploaded for a run (<c>POST /api/run-input-files</c>). The Core holds it encrypted until
/// a run request names it (<see cref="RunRequest.InputFiles"/>) — only the uploading principal's —
/// or it expires unused at <see cref="ExpiresUtc"/>; it is deleted when its run ends.
/// <see cref="Sha256"/> is the content's hex digest.
/// </summary>
public sealed record RunInputFile(Guid Id, long Length, string Sha256, DateTimeOffset ExpiresUtc);

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

    /// <summary>
    /// The principal the run runs for — on a delegated start (<see cref="RunRequest.RequestedBy"/>)
    /// the one it was started for. Null for a platform dispatch (only administrators see those).
    /// </summary>
    public Guid? RunsFor { get; init; }

    /// <summary>The principal that started the run on <see cref="RunsFor"/>'s behalf; null when they are the same.</summary>
    public Guid? StartedBy { get; init; }

    /// <summary>Who else may observe and steer the run (set by <see cref="RunsFor"/>, <see cref="StartedBy"/> or an administrator).</summary>
    public IReadOnlyList<AccessGrant> Grants { get; init; } = [];

    /// <summary>
    /// How the run ended in its workflow's own words — a short reason such as <c>idle-timeout</c>,
    /// set with the final state when the workflow gave one (its documentation names the
    /// vocabulary); null while the run is in flight.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Replaces the subjects a run is shared with — principals, platform groups or directory groups,
/// exactly like a connector's grants. A run is otherwise visible and steerable only by the
/// principal it runs for, the one that started it on their behalf, and administrators.
/// </summary>
public sealed record SetRunGrants(IReadOnlyList<AccessGrant> Grants);

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
/// frames. <see cref="Sequence"/> counts the run's items across all its views (1, 2, 3, …): the
/// order they were published in, and the position a stream resumes from.
/// </summary>
public sealed record RunViewItem(
    string ViewName,
    long Sequence,
    string PayloadJson,
    DateTimeOffset TimestampUtc);

/// <summary>
/// One frame of a run's live-view stream (SSE). A discriminated envelope so a client can tell a
/// lifecycle transition from a view item without a second round-trip: <see cref="Kind"/> is
/// <c>"status"</c> (the payload is a serialized <c>WorkflowStatusEvent</c> — the run's state as
/// the Core holds it), <c>"view"</c> (the payload is a serialized <c>ViewDataMessage</c>) or
/// <c>"gap"</c> (view items the Core did not retain — see <see cref="AsGap"/>).
/// <see cref="Sequence"/> is the view item's run-wide sequence for view frames, and <c>0</c>
/// otherwise.
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
    public const string GapKind = "gap";

    /// <summary>
    /// The stream's position after this frame (also its SSE <c>id</c>). Opaque: send the last one
    /// you received back as <c>Last-Event-ID</c> (or <c>?after=</c>) and the stream resumes right
    /// after it — every frame exactly once, in order. Null only on frames from a Core that does not
    /// resume streams.
    /// </summary>
    public string? Cursor { get; init; }

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

    /// <summary>The missing range of a <see cref="GapKind"/> frame; null otherwise.</summary>
    public RunStreamGap? AsGap()
        => Kind == GapKind ? TryDeserialize<RunStreamGap>() : null;

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
    Guid? CommandId = null,
    /// <summary>How the run ended in its workflow's own words (<see cref="RunStatus.Reason"/>).</summary>
    string? Reason = null)
{
    public bool IsTerminal => RunStates.IsTerminal(State);
}

/// <summary>The typed shape of a view stream frame: one item of a declared live view.</summary>
public sealed record RunStreamView(
    Guid WorkflowInstanceId,
    string ViewName,
    long Sequence,
    string PayloadJson);

/// <summary>
/// View items <see cref="FromSequence"/>..<see cref="ToSequence"/> (inclusive) that the stream
/// cannot deliver: published while this client was not connected and not retained by the Core
/// (beyond the run's persisted-item cap) — the stream goes on after them.
/// </summary>
public sealed record RunStreamGap(long FromSequence, long ToSequence);

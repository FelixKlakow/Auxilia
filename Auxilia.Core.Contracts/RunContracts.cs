namespace Auxilia.Core.Contracts;

/// <summary>
/// Start a workflow run directly, without a stored configuration — the "configured on the
/// fly" path. The caller supplies everything the Core needs to hand the runner a
/// self-contained spec.
/// </summary>
public sealed record RunRequest(
    string WorkflowType,
    string PackageUri,
    IReadOnlyDictionary<string, string>? Context = null,
    Guid? RequestedBy = null,
    IReadOnlyList<SlotBinding>? SlotBindings = null,
    IReadOnlyList<RepositorySpec>? Repositories = null);

/// <summary>
/// A repository to prepare into a run's workspace before launch (cloned by the Workspace Manager and
/// bind-mounted). The URL is non-secret and supplied per run; when the repo needs authentication,
/// <see cref="AuthConnectorId"/> references a Core connector that holds <em>only the credential</em>
/// (e.g. a TFS/Azure DevOps PAT). The credential is resolved just-in-time at dispatch and never rides
/// the bus or enters the workflow container — one auth connector can serve many repositories.
/// </summary>
public sealed record RepositorySpec(
    string Id,
    string CloneUrl,
    string? Branch = null,
    Guid? AuthConnectorId = null,
    bool NoCache = false);

/// <summary>Acknowledgement that a run was accepted and dispatched to the runner.</summary>
public sealed record RunAccepted(Guid RunId, Guid CommandId);

/// <summary>Point-in-time status of a run, resolved from the Core's run-instance store.</summary>
public sealed record RunStatus(
    Guid RunId,
    string WorkflowType,
    string State,
    string? Error,
    DateTimeOffset CreatedUtc,
    Guid? ConfigurationId,
    string? ConfigurationName);

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

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
    IReadOnlyList<SlotBinding>? SlotBindings = null);

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

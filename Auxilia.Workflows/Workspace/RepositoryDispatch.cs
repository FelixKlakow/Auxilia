namespace Auxilia.Workflows.Workspace;

/// <summary>
/// A per-run repository carried on the dispatch command: the (non-secret) clone URL and branch, and —
/// when the repo needs authentication — the name of the synthetic slot the runner resolves at dispatch
/// to obtain the credential (which never rides the bus). The runner combines them into a credentialed
/// clone URL, clones, and strips the credential before the workflow container sees the working copy.
/// </summary>
public sealed record RepositoryDispatch(
    string Id,
    string CloneUrl,
    string? Branch = null,
    string? AuthSlotName = null,
    bool NoCache = false);

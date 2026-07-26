namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// The setting-role vocabulary the runner's git workspace materializer understands. Roles are
/// declared on provider setting descriptors and ride a <c>WorkspaceMountDispatch</c> keyed by role;
/// the control plane and its clients never interpret them — this class is the single place the
/// meaning is defined. A future non-git materializer brings its own roles without touching these.
/// </summary>
internal static class WorkspaceMountRoles
{
    /// <summary>The URL git clones into the run's workspace (required by the git materializer).</summary>
    public const string CloneUrl = "clone-url";

    /// <summary>The branch to clone; the remote's default branch when absent.</summary>
    public const string Branch = "branch";

    /// <summary>Subdirectory of the clone the workflow should treat as the mount's root.</summary>
    public const string WorkingDirectory = "working-directory";

    /// <summary>"true" clones fresh per run and never touches the warm cache (sensitive repos).</summary>
    public const string NoCache = "no-cache";
}

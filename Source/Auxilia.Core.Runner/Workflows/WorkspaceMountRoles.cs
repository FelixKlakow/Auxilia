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

    /// <summary>
    /// "true" keeps the (scoped) credential configured on the per-run clone so the workflow can
    /// push — Model B of the credential decision; the agent's per-action permission policy
    /// governs each push. Push mounts always clone fresh, never via the warm cache.
    /// </summary>
    public const string AllowPush = "allow-push";

    /// <summary>Commit author name configured on the clone (local git config).</summary>
    public const string CommitName = "commit-name";

    /// <summary>Commit author e-mail configured on the clone (local git config).</summary>
    public const string CommitEmail = "commit-email";
}

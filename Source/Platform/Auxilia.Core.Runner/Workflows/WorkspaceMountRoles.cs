namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// The setting-role vocabulary the runner's workspace materializers understand. Roles are
/// declared on provider setting descriptors and ride a <c>WorkspaceMountDispatch</c> keyed by role;
/// the control plane and its clients never interpret them — this class is the single place the
/// meaning is defined. The roles a mount carries also select its materializer: a mount WITH
/// <see cref="CloneUrl"/> is materialized by git; one without it (and without a credential)
/// becomes a fresh empty scratch directory (the <c>empty-workspace</c> provider).
/// </summary>
internal static class WorkspaceMountRoles
{
    /// <summary>The URL git clones into the run's workspace (selects the git materializer).</summary>
    public const string CloneUrl = "clone-url";

    /// <summary>The branch to clone; the remote's default branch when absent.</summary>
    public const string Branch = "branch";

    /// <summary>
    /// Subdirectory of the mount the workflow should treat as its root — of the clone for git
    /// mounts, pre-created by the materializer for empty workspaces.
    /// </summary>
    public const string WorkingDirectory = "working-directory";

    /// <summary>"true" clones fresh per run and never touches the warm cache (sensitive repos).</summary>
    public const string NoCache = "no-cache";

    /// <summary>
    /// "true" keeps the (scoped) credential configured on the per-run clone so the workflow can
    /// push — Model B of the credential decision; the agent's per-action permission policy
    /// governs each push. Push mounts always clone fresh, never via the warm cache.
    /// </summary>
    public const string AllowPush = "allow-push";

    /// <summary>
    /// Post-binding setup script announced to the container (never run by the runner): the SDK
    /// executes it in the mount's root before the application starts. Whoever may bind the
    /// mount already controls the repository content the workflow will act on — the script adds
    /// no authority beyond it, and runs under the run's egress policy.
    /// </summary>
    public const string SetupScript = "setup-script";

    /// <summary>Commit author name configured on the clone (local git config).</summary>
    public const string CommitName = "commit-name";

    /// <summary>Commit author e-mail configured on the clone (local git config).</summary>
    public const string CommitEmail = "commit-email";
}

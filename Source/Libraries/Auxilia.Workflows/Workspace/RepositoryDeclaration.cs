namespace Auxilia.Workflows.Workspace;

/// <summary>
/// A repository the workflow declares it needs in its workspace (ARCHITECTURE §9) —
/// the Workspace Manager prepares a per-run copy under <c>/workspace/repos/&lt;Id&gt;</c>
/// before launch. <paramref name="NoCache"/> repositories are cloned fresh per run and
/// never enter the warm cache.
/// </summary>
public sealed record RepositoryDeclaration(
    string Id, string CloneUrl, string? Branch = null, bool NoCache = false)
{
    /// <summary>
    /// The run may push back to this repository: the (scoped) credential stays configured on
    /// the per-run clone — Model B of the credential decision. Push mounts never use the
    /// warm cache.
    /// </summary>
    public bool AllowPush { get; init; }

    /// <summary>
    /// Credentialed remote URL carrying a push-scoped token (when the connector provides one):
    /// the clone still uses <see cref="CloneUrl"/>'s credential, but THIS becomes the origin
    /// the container sees — the workflow never holds more authority than pushing needs.
    /// </summary>
    public string? PushCloneUrl { get; init; }

    /// <summary>Commit identity configured on the clone; agents never guess an authorship.</summary>
    public string? CommitName { get; init; }

    public string? CommitEmail { get; init; }

    /// <summary>
    /// Post-binding setup (restore, codegen, bootstrap) the repository needs before the workflow
    /// can act on it. Runs INSIDE the workflow container in the repository's root, after the
    /// workspace is bound and before the application — under the run's egress policy. Part of
    /// the signed manifest, so signature trust covers it.
    /// </summary>
    public string? SetupScript { get; init; }
}

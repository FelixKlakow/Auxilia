namespace Auxilia.Workflows.Workspace;

/// <summary>
/// A repository the workflow declares it needs in its workspace (ARCHITECTURE §9) —
/// the Workspace Manager prepares a per-run copy under <c>/workspace/repos/&lt;Id&gt;</c>
/// before launch. <paramref name="NoCache"/> repositories are cloned fresh per run and
/// never enter the warm cache.
/// </summary>
public sealed record RepositoryDeclaration(
    string Id, string CloneUrl, string? Branch = null, bool NoCache = false);

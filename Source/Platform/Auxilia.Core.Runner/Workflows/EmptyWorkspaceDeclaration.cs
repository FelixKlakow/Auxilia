namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// A workspace mount materialized as a fresh, empty scratch directory — the non-git workspace
/// materializer (ARCHITECTURE §9): no clone, no credential, same <c>repos/&lt;Id&gt;</c> layout
/// as repository mounts, deleted with the run root at terminal state.
/// <see cref="WorkingDirectory"/> (when bound) is pre-created so the announced mount root exists.
/// </summary>
public sealed record EmptyWorkspaceDeclaration(string Id, string? WorkingDirectory = null);

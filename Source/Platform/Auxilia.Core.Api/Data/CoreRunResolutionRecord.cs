using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// Per-run credential resolution context: the run's slot→connector bindings plus the run-scoped
/// resolution token the runner presents to resolve them just-in-time. **No secrets are stored
/// here** — only connector references, resolved against <c>ConnectorService</c> at resolution time.
/// </summary>
public sealed record CoreRunResolutionRecord : IEntity
{
    /// <summary>The run id (= dispatch <c>CommandId</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>
    /// SHA-256 digest of the opaque token minted at dispatch; the runner must present the token
    /// to resolve any slot of this run. Only the digest is stored — the Core verifies, never replays.
    /// </summary>
    public required string ResolutionTokenHash { get; init; }

    /// <summary>The run's slot bindings (connector references / inline provider settings), serialized as JSON.</summary>
    public string SlotBindingsJson { get; init; } = "[]";

    /// <summary>
    /// The principal that triggered the run, if any. Used to resolve on-behalf-of (OBO) delegated
    /// slots as that user at resolution time; still no secret is stored here.
    /// </summary>
    public Guid? TriggeredByPrincipalId { get; init; }

    /// <summary>
    /// The serialized <see cref="Auxilia.Workflows.Messaging.Messages.RunWorkflowCommand"/> dispatched for
    /// this run (keyed by its <c>CommandId</c>). Kept so an orphaned run can be re-dispatched once on
    /// failover; the resolution token and the token-authorized package URL are redacted before
    /// persistence — a re-dispatch mints fresh ones.
    /// </summary>
    public string? DispatchCommandJson { get; init; }

    /// <summary>
    /// Ids of the stored workspaces the run's bindings expanded from (JSON array) — a rerun
    /// re-gates each against the rerunning principal, like the connectors.
    /// </summary>
    public string WorkspaceIdsJson { get; init; } = "[]";

    /// <summary>The stored configuration the run was dispatched from, if any — a rerun re-applies its visibility.</summary>
    public Guid? ConfigurationId { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }
}

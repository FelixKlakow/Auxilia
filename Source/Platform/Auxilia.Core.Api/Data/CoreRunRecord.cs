using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// The Core's view of a run, built from the runner's lifecycle status events on the bus. Id is
/// the runner-assigned workflow instance id.
/// </summary>
public sealed record CoreRunRecord : IVersionedEntity
{
    public Guid Id { get; init; }

    /// <summary>
    /// Store-managed optimistic-concurrency version (see <see cref="IVersionedEntity"/>) backing
    /// the compare-and-swap writes of the run-tracking terminal sink and the claim-timeout sweep.
    /// Never assigned by callers.
    /// </summary>
    public long Version { get; set; }
    public required string WorkflowType { get; init; }
    public required string State { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>When the run reached its terminal state; null while it is still in flight.</summary>
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>How the run ended in the workflow's own words (e.g. <c>idle-timeout</c>); null while in flight or when it gave none.</summary>
    public string? Reason { get; init; }

    /// <summary>How many view items the run published, as it reported with its final state; null until then.</summary>
    public long? LastViewSequence { get; init; }

    public Guid? ConfigurationId { get; init; }
    public string? ConfigurationName { get; init; }

    /// <summary>
    /// Service id of the Core.Runner that owns this run, learned from the run's claim status event.
    /// Lets the failover monitor find a dead runner's non-terminal runs without reading the runner DB.
    /// </summary>
    public Guid? OwnerServiceId { get; init; }

    /// <summary>
    /// The dispatch <c>CommandId</c> this run was launched from (stamped from the claim status
    /// event). A dispatch caller only knows this id — endpoints addressing a run accept either.
    /// </summary>
    public Guid? CommandId { get; init; }

    /// <summary>
    /// The serialized <see cref="Auxilia.Workflows.Messaging.Messages.RunWorkflowCommand"/> this run was
    /// dispatched from, recovered (via the dispatch <c>CommandId</c>) so an orphaned run can be
    /// re-dispatched once on failover. Present only once the run's claim event has been correlated.
    /// </summary>
    public string? DispatchCommandJson { get; init; }

    /// <summary>
    /// Core-reachable address of the run's interactive web terminal (ttyd), learned from the
    /// runner's status events. Only the Core's authenticated terminal proxy ever dials it —
    /// the address itself never leaves the Core.
    /// </summary>
    public string? TerminalEndpoint { get; init; }

    /// <summary>
    /// The principal the run runs for (the triggering principal; on a delegated start, the one it
    /// was started for). Stamped at dispatch and preserved through the claim rekey. Null for a
    /// platform dispatch — such a run is visible to administrators only.
    /// </summary>
    public Guid? RunsForPrincipalId { get; init; }

    /// <summary>The principal that started the run on <see cref="RunsForPrincipalId"/>'s behalf; null when they are the same.</summary>
    public Guid? StartedByPrincipalId { get; init; }

    /// <summary>Explicit grants (serialized <c>AccessGrant</c> list): who else may observe and steer the run.</summary>
    public string? GrantsJson { get; init; }
}

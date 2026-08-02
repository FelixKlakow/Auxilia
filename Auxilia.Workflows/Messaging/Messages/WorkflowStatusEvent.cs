namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Published by the Core.Runner to the <c>workflow.status</c> topic exchange on every lifecycle
/// transition, keyed by run identity (<see cref="RoutingKeyFor"/>) so nodes ingest only the runs
/// they have an audience for. Failures, retries, and failovers are never silent: the Core.Api
/// consumes these for tracking and SSE so users always see why a run changed state.
/// </summary>
public sealed record WorkflowStatusEvent(
    Guid WorkflowInstanceId,
    string WorkflowType,
    /// <summary>Lifecycle state name: Received, PreFlightFailed, Queued, Running, Success, Failed, Cancelled, Draining.</summary>
    string State,
    string? ErrorMessage,
    DateTimeOffset TimestampUtc,
    /// <summary>
    /// Service id of the Core.Runner that owns this run. Populated on the claim (first) transition so a
    /// consumer can attribute the run to a runner without reading the runner's database. Optional for
    /// back-compat: later transitions may omit it and consumers must preserve the last non-null value.
    /// </summary>
    Guid? OwnerServiceId = null,
    /// <summary>
    /// The dispatch <c>CommandId</c> this run was launched from — lets a consumer correlate the
    /// runner-assigned instance id back to the originating command (e.g. to recover the stored dispatch
    /// command for failover re-dispatch). Optional for back-compat.
    /// </summary>
    Guid? CommandId = null,
    /// <summary>
    /// Where the run's interactive web terminal (ttyd) is reachable FROM THE CORE — never handed
    /// to end clients, which only ever talk to the Core's authenticated terminal proxy. Stamped
    /// on the transition after launch; consumers preserve the last non-null value.
    /// </summary>
    string? TerminalEndpoint = null)
{
    // Topic exchange (selective routing) — a NEW name, because the retired fanout
    // "workflow.status-events" cannot be redeclared with a different type in place.
    public const string ExchangeName = "workflow.status";

    /// <summary>
    /// Routing key at publish: the instance id, extended with the originating command id on the
    /// claim transition so a subscriber that only knows the dispatch command id still matches.
    /// </summary>
    public static string RoutingKeyFor(Guid instanceId, Guid? commandId = null)
        => commandId is { } c && c != instanceId ? $"{instanceId}.{c}" : instanceId.ToString();

    /// <summary>
    /// Binding keys for a subscriber interested in <paramref name="runId"/> under EITHER identity:
    /// first-word match (instance id; <c>#</c> also matches the single-word key) and
    /// second-word match (command id on the claim transition).
    /// </summary>
    public static IReadOnlyList<string> BindingKeysFor(Guid runId) => [$"{runId}.#", $"*.{runId}"];
}

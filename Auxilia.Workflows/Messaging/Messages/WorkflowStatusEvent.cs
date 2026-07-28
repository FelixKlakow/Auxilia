namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Published by the Core.Runner to the <c>workflow.status-events</c> fanout exchange on
/// every lifecycle transition. Failures, retries, and failovers are never silent: the Backend
/// Service consumes these for the dashboard (and MCP) so users always see why a run changed state.
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
    public const string ExchangeName = "workflow.status-events";
}

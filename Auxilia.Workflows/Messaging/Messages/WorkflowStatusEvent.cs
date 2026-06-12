namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Published by the Steering Instance to the <c>workflow.status-events</c> fanout exchange on
/// every lifecycle transition. Failures, retries, and failovers are never silent: the Backend
/// Service consumes these for the dashboard (and MCP) so users always see why a run changed state.
/// </summary>
public sealed record WorkflowStatusEvent(
    Guid WorkflowInstanceId,
    string WorkflowType,
    /// <summary>Lifecycle state name: Received, PreFlightFailed, Queued, Running, Success, Failed, Cancelled, Draining.</summary>
    string State,
    string? ErrorMessage,
    DateTimeOffset TimestampUtc)
{
    public const string ExchangeName = "workflow.status-events";
}

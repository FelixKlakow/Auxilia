namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Periodic liveness beat published by each Core.Runner to a fanout exchange so a monitor can track
/// runner liveness over the bus — without reading the runner's database. A runner whose beat goes
/// stale is treated as dead and its owned non-terminal runs are failed over. The beat also
/// advertises the host platform the runner's Docker daemon can execute (null until probed), so
/// environment editors can tell which base names the fleet actually hosts.
/// </summary>
public sealed record RunnerHeartbeat(
    Guid ServiceId,
    string ServiceName,
    DateTimeOffset TimestampUtc,
    string? HostPlatform = null,
    string? HostArchitecture = null)
{
    public const string ExchangeName = "platform.runner-heartbeats";
}

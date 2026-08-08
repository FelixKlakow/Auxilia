namespace Auxilia.Core.Contracts;

/// <summary>
/// One runner of the fleet as the Core knows it from bus heartbeats: liveness plus the host
/// platform its Docker daemon advertises (null until the runner has probed its daemon). Served
/// by <c>GET /api/runners</c> so environment editors can tell which base names the fleet hosts.
/// </summary>
public sealed record RunnerDto(
    Guid ServiceId,
    string? ServiceName,
    DateTimeOffset LastSeenUtc,
    bool Alive,
    string? HostPlatform,
    string? HostArchitecture);

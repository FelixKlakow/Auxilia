using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Liveness heartbeat of a platform service instance; Id is the service's instance ID.
/// The Backend Service's heartbeat monitor treats a stale beat as a dead Steering Instance
/// and fails over its owned workflow runs.
/// </summary>
public sealed record ServiceHeartbeatRecord : IEntity
{
    public Guid Id { get; init; }
    public required string ServiceName { get; init; }
    public DateTimeOffset LastBeatUtc { get; init; }
}

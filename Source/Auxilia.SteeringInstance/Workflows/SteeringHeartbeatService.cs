using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Periodically writes this Steering Instance's liveness heartbeat to the platform data layer.
/// The Backend Service's heartbeat monitor fails over owned runs when the beat goes stale.
/// </summary>
public sealed class SteeringHeartbeatService(
    IDataAccess<ServiceHeartbeatRecord> heartbeats,
    SteeringInstanceInfo instanceInfo,
    TimeProvider timeProvider,
    IOptions<WorkflowDispatcherSettings> settings,
    ILogger<SteeringHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.HeartbeatIntervalSeconds));
        using var timer = new PeriodicTimer(interval, timeProvider);

        logger.LogInformation("Steering heartbeat started. Interval={Interval}s", interval.TotalSeconds);

        do
        {
            try
            {
                await heartbeats.SaveAsync(new ServiceHeartbeatRecord
                {
                    Id = instanceInfo.ServiceId,
                    ServiceName = "Auxilia.SteeringInstance",
                    LastBeatUtc = timeProvider.GetUtcNow()
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to write heartbeat — will retry next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

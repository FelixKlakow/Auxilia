using Auxilia.Messaging;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Periodically advertises this runner's liveness two ways: it writes a durable
/// <see cref="ServiceHeartbeatRecord"/> to its own database AND publishes a <see cref="RunnerHeartbeat"/>
/// on the bus. The bus beat lets a monitor in another service (e.g. Core.Api) detect a dead runner and
/// fail over its owned runs without ever reading this runner's database.
/// </summary>
public sealed class RunnerHeartbeatService(
    IDataAccess<ServiceHeartbeatRecord> heartbeats,
    IMessageBusClient messageBus,
    CoreRunnerInfo instanceInfo,
    TimeProvider timeProvider,
    IOptions<WorkflowDispatcherSettings> settings,
    ILogger<RunnerHeartbeatService> logger) : BackgroundService
{
    private const string ServiceName = "Auxilia.Core.Runner";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.HeartbeatIntervalSeconds));
        using var timer = new PeriodicTimer(interval, timeProvider);

        await messageBus.DeclareExchangeAsync(RunnerHeartbeat.ExchangeName, stoppingToken);
        logger.LogInformation("Runner heartbeat started. Interval={Interval}s", interval.TotalSeconds);

        do
        {
            var now = timeProvider.GetUtcNow();
            try
            {
                await heartbeats.SaveAsync(new ServiceHeartbeatRecord
                {
                    Id = instanceInfo.ServiceId,
                    ServiceName = ServiceName,
                    LastBeatUtc = now
                }, stoppingToken);
                await messageBus.PublishToExchangeAsync(RunnerHeartbeat.ExchangeName,
                    new RunnerHeartbeat(instanceInfo.ServiceId, ServiceName, now), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to write heartbeat — will retry next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

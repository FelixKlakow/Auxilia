using Auxilia.Messaging;
using Microsoft.Extensions.Configuration;

namespace Auxilia.BackendService;

/// <summary>
///     Hosted service that declares the BackendService queue on startup.
///     The queue name defaults to <see cref="DefaultQueueName" /> but can be overridden
///     via the <c>BackendService:QueueName</c> configuration key (env var <c>BackendService__QueueName</c>).
/// </summary>
public sealed class QueueInitializer(
    IMessageBusClient messageBusClient,
    IConfiguration configuration,
    ILogger<QueueInitializer> logger) : IHostedService
{
    /// <summary>Fallback queue name used when no configuration override is present.</summary>
    public const string DefaultQueueName = "backend-service";

    /// <summary>Resolved queue name for this instance (config override or default).</summary>
    public string QueueName => configuration["BackendService:QueueName"] ?? DefaultQueueName;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Declaring queue '{Queue}'", QueueName);
        await messageBusClient.DeclareQueueAsync(QueueName, cancellationToken);
        logger.LogInformation("Queue '{Queue}' declared successfully", QueueName);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
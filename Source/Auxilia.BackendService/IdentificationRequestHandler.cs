using Auxilia.Messaging;
using Auxilia.Messaging.Messages;
using Microsoft.Extensions.Configuration;

namespace Auxilia.BackendService;

/// <summary>
///     Listens on the BackendService queue and responds to <see cref="IdentificationRequestMessage" />
///     with an <see cref="IdentificationResponseMessage" />.
/// </summary>
public sealed class IdentificationRequestHandler(
    IMessageBusClient messageBusClient,
    ServiceInfo serviceInfo,
    IConfiguration configuration,
    ILogger<IdentificationRequestHandler> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    private string QueueName => configuration["BackendService:QueueName"] ?? QueueInitializer.DefaultQueueName;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = await messageBusClient.SubscribeAsync<IdentificationRequestMessage>(
            QueueName,
            HandleAsync,
            cancellationToken);

        logger.LogInformation("Listening for identification requests on '{Queue}'", QueueName);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private async Task HandleAsync(IdentificationRequestMessage request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Received identification request {MessageId} from {RequestingServiceId}",
            request.MessageId, request.RequestingServiceId);

        var response = new IdentificationResponseMessage(
            serviceInfo.ServiceId,
            "AuxiliaBackendService",
            serviceInfo.Version,
            serviceInfo.StartupTimeUtc);

        await messageBusClient.PublishAsync(request.ResponseTopic, response, cancellationToken);

        logger.LogInformation("Sent identification response to '{ResponseTopic}'", request.ResponseTopic);
    }
}
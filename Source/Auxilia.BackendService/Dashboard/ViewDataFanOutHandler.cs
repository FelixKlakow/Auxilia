using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.AspNetCore.SignalR;

namespace Auxilia.BackendService.Dashboard;

/// <summary>
/// Pushes live view items from the bus to subscribed SignalR circuits (ARCHITECTURE §15).
/// Persistence happens on the Steering Instance; this is the live path only — clients
/// re-sync via replay, so live data is never ahead of the persisted record for long.
/// </summary>
public sealed class ViewDataFanOutHandler(
    IMessageBusClient messageBus,
    IHubContext<ViewDataHub> hub,
    LiveViewBroker broker,
    ILogger<ViewDataFanOutHandler> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await messageBus.DeclareExchangeAsync(ViewDataMessage.ExchangeName, cancellationToken);
        _subscription = await messageBus.SubscribeToExchangeAsync<ViewDataMessage>(
            ViewDataMessage.ExchangeName, HandleAsync, cancellationToken);

        logger.LogInformation("ViewDataFanOutHandler started — listening on {Exchange}.",
            ViewDataMessage.ExchangeName);
    }

    private Task HandleAsync(ViewDataMessage message, CancellationToken ct)
    {
        // In-process circuits first (no backplane), then the hub for external clients.
        broker.Publish(message);
        return hub.Clients
            .Group(ViewDataHub.ViewGroup(message.WorkflowInstanceId, message.ViewName))
            .SendAsync("ViewData", message, ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

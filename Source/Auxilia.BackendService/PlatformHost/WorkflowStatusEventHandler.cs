using Auxilia.BackendService.Dashboard;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.AspNetCore.SignalR;

namespace Auxilia.BackendService.PlatformHost;

/// <summary>
/// Consumes lifecycle status events from the Steering Instance pool and pushes them to
/// subscribed dashboard circuits (ARCHITECTURE §14.3) — failures and restarts are never
/// silent.
/// </summary>
public sealed class WorkflowStatusEventHandler(
    IMessageBusClient messageBus,
    IHubContext<ViewDataHub> hub,
    LiveViewBroker broker,
    ILogger<WorkflowStatusEventHandler> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await messageBus.DeclareExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        _subscription = await messageBus.SubscribeToExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, HandleAsync, cancellationToken);

        logger.LogInformation("WorkflowStatusEventHandler started — listening on {Exchange}.",
            WorkflowStatusEvent.ExchangeName);
    }

    private async Task HandleAsync(WorkflowStatusEvent statusEvent, CancellationToken ct)
    {
        logger.LogInformation(
            "Workflow status: Instance={InstanceId} Type={WorkflowType} State={State} Error={Error}",
            statusEvent.WorkflowInstanceId, statusEvent.WorkflowType,
            statusEvent.State, statusEvent.ErrorMessage ?? "<none>");

        broker.Publish(statusEvent);
        await hub.Clients.Group(ViewDataHub.AllRunsGroup).SendAsync("RunStatus", statusEvent, ct);
        await hub.Clients.Group(ViewDataHub.RunGroup(statusEvent.WorkflowInstanceId))
            .SendAsync("RunStatus", statusEvent, ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

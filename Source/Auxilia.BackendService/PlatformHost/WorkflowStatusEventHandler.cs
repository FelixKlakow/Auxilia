using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.BackendService.PlatformHost;

/// <summary>
/// Consumes lifecycle status events from the Steering Instance pool. Currently logs them;
/// the dashboard work adds SignalR backplane fan-out on top of this consumer so users see
/// every failure and restart live (ARCHITECTURE §14.3).
/// </summary>
public sealed class WorkflowStatusEventHandler(
    IMessageBusClient messageBus,
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

    private Task HandleAsync(WorkflowStatusEvent statusEvent, CancellationToken ct)
    {
        logger.LogInformation(
            "Workflow status: Instance={InstanceId} Type={WorkflowType} State={State} Error={Error}",
            statusEvent.WorkflowInstanceId, statusEvent.WorkflowType,
            statusEvent.State, statusEvent.ErrorMessage ?? "<none>");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

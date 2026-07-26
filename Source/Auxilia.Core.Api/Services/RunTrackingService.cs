using Auxilia.Core.Api.Data;
using Auxilia.Messaging;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Builds the Core's run view from the runner's lifecycle events. Subscribes to the
/// <see cref="WorkflowStatusEvent.ExchangeName"/> fanout (its own exclusive queue — it competes
/// with no one) and upserts a <see cref="CoreRunRecord"/> per instance on every transition.
/// </summary>
public sealed class RunTrackingService(
    IMessageBusClient bus,
    IDataAccess<CoreRunRecord> runs,
    IDataAccess<CoreRunResolutionRecord> resolutions,
    ILogger<RunTrackingService> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, HandleAsync, cancellationToken);
        logger.LogInformation(
            "RunTrackingService listening on {Exchange}.", WorkflowStatusEvent.ExchangeName);
    }

    private async Task HandleAsync(WorkflowStatusEvent statusEvent, CancellationToken ct)
    {
        var existing = await runs.ReadAsync(statusEvent.WorkflowInstanceId, ct);

        // Ownership + the dispatch command are stamped once (on the claim event) and then preserved —
        // later transitions may omit them. The dispatch command is recovered from the Core's own
        // resolution store via the command id, never from the runner's database.
        var ownerServiceId = statusEvent.OwnerServiceId ?? existing?.OwnerServiceId;
        var dispatchCommandJson = existing?.DispatchCommandJson;
        if (dispatchCommandJson is null && statusEvent.CommandId is { } commandId)
            dispatchCommandJson = (await resolutions.ReadAsync(commandId, ct))?.DispatchCommandJson;

        await runs.SaveAsync(new CoreRunRecord
        {
            Id = statusEvent.WorkflowInstanceId,
            WorkflowType = statusEvent.WorkflowType,
            State = statusEvent.State,
            ErrorMessage = statusEvent.ErrorMessage,
            CreatedUtc = existing?.CreatedUtc ?? statusEvent.TimestampUtc,
            UpdatedUtc = statusEvent.TimestampUtc,
            ConfigurationId = existing?.ConfigurationId,
            ConfigurationName = existing?.ConfigurationName,
            OwnerServiceId = ownerServiceId,
            DispatchCommandJson = dispatchCommandJson
        }, ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

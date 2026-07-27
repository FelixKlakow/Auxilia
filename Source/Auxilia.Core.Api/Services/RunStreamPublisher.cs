using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Bridges the bus live-view feeds to the SSE <see cref="RunStreamBroker"/>: subscribes to the
/// <c>workflow.status-events</c> and <c>workflow.view-data</c> fanouts (its own exclusive queues —
/// the persisting <see cref="RunTrackingService"/> gets its own copy) and re-emits each message as a
/// discriminated <see cref="RunStreamEvent"/>. Live push only; persistence stays with
/// <see cref="RunTrackingService"/> and the Core.Runner.
/// </summary>
public sealed class RunStreamPublisher(
    IMessageBusClient bus,
    RunStreamBroker broker,
    ILogger<RunStreamPublisher> logger) : IHostedService
{
    private IAsyncDisposable? _statusSubscription;
    private IAsyncDisposable? _viewSubscription;

    // A dispatch is acknowledged with its CommandId, but the runner emits events under its own
    // WorkflowInstanceId. Status events carry the originating CommandId (the claim transition), so
    // every event is re-published under BOTH ids — a client may observe the id the dispatch gave it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid> _commandIdByInstance = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await bus.DeclareExchangeAsync(ViewDataMessage.ExchangeName, cancellationToken);

        _statusSubscription = await bus.SubscribeToExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, HandleStatusAsync, cancellationToken);
        _viewSubscription = await bus.SubscribeToExchangeAsync<ViewDataMessage>(
            ViewDataMessage.ExchangeName, HandleViewAsync, cancellationToken);

        logger.LogInformation(
            "RunStreamPublisher listening on {StatusExchange} and {ViewExchange}.",
            WorkflowStatusEvent.ExchangeName, ViewDataMessage.ExchangeName);
    }

    private Task HandleStatusAsync(WorkflowStatusEvent statusEvent, CancellationToken ct)
    {
        if (statusEvent.CommandId is { } commandId && commandId != statusEvent.WorkflowInstanceId)
            _commandIdByInstance[statusEvent.WorkflowInstanceId] = commandId;

        PublishAliased(statusEvent.WorkflowInstanceId, runId => new RunStreamEvent(
            RunStreamEvent.StatusKind,
            runId,
            Sequence: 0,
            PayloadJson: JsonSerializer.Serialize(statusEvent, JsonSerializerOptions.Web),
            TimestampUtc: statusEvent.TimestampUtc));

        if (CoreRunStates.IsTerminal(statusEvent.State))
            _commandIdByInstance.TryRemove(statusEvent.WorkflowInstanceId, out _);
        return Task.CompletedTask;
    }

    private Task HandleViewAsync(ViewDataMessage message, CancellationToken ct)
    {
        PublishAliased(message.WorkflowInstanceId, runId => new RunStreamEvent(
            RunStreamEvent.ViewKind,
            runId,
            message.Sequence,
            PayloadJson: JsonSerializer.Serialize(message, JsonSerializerOptions.Web),
            TimestampUtc: DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    /// <summary>Publishes under the instance id and, when known, the originating command id too.</summary>
    private void PublishAliased(Guid instanceId, Func<Guid, RunStreamEvent> eventFor)
    {
        broker.Publish(eventFor(instanceId));
        if (_commandIdByInstance.TryGetValue(instanceId, out var commandId))
            broker.Publish(eventFor(commandId));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_statusSubscription is not null)
            await _statusSubscription.DisposeAsync();
        if (_viewSubscription is not null)
            await _viewSubscription.DisposeAsync();
    }
}

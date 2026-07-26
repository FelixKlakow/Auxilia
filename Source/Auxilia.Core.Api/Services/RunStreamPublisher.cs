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
        broker.Publish(new RunStreamEvent(
            RunStreamEvent.StatusKind,
            statusEvent.WorkflowInstanceId,
            Sequence: 0,
            PayloadJson: JsonSerializer.Serialize(statusEvent, JsonSerializerOptions.Web),
            TimestampUtc: statusEvent.TimestampUtc));
        return Task.CompletedTask;
    }

    private Task HandleViewAsync(ViewDataMessage message, CancellationToken ct)
    {
        broker.Publish(new RunStreamEvent(
            RunStreamEvent.ViewKind,
            message.WorkflowInstanceId,
            message.Sequence,
            PayloadJson: JsonSerializer.Serialize(message, JsonSerializerOptions.Web),
            TimestampUtc: DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_statusSubscription is not null)
            await _statusSubscription.DisposeAsync();
        if (_viewSubscription is not null)
            await _viewSubscription.DisposeAsync();
    }
}

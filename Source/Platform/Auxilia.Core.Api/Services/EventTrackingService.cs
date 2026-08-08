using Auxilia.Core.Api.Data;
using Auxilia.Messaging;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Mirrors each <see cref="WorkflowEventMessage"/> into the Core's own
/// <see cref="CoreEventRecord"/> store (shared queue — with N Core.Api nodes exactly one
/// mirrors each event; the live <see cref="EventStreamPublisher"/> keeps its selectively-bound
/// per-node copy for SSE). Keyed by event id — re-delivery is an idempotent upsert. Also sweeps
/// rows older than <see cref="CoreApiSettings.EventRetentionDays"/>; a catch-up query can only
/// reach back that far, which comfortably covers reconnect gaps.
/// </summary>
public sealed class EventTrackingService(
    IMessageBusClient bus,
    IDataAccess<CoreEventRecord> events,
    IOptions<CoreApiSettings> settings,
    ILogger<EventTrackingService> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private IAsyncDisposable? _subscription;
    private CancellationTokenSource? _lifetime;
    private Task? _sweeper;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareTopicExchangeAsync(WorkflowEventMessage.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToTopicExchangeSharedAsync<WorkflowEventMessage>(
            WorkflowEventMessage.ExchangeName, "core-api.event-tracking", "#", HandleAsync, cancellationToken);
        _lifetime = new CancellationTokenSource();
        if (settings.Value.EventRetentionDays > 0)
            _sweeper = SweepLoopAsync(_lifetime.Token);
        logger.LogInformation("EventTrackingService listening on {Exchange}.",
            WorkflowEventMessage.ExchangeName);
    }

    private Task HandleAsync(WorkflowEventMessage evt, CancellationToken ct)
    {
        // Reserved run.* events must be the platform's own (deterministic id) — a container
        // hand-rolling a bus client cannot mint arbitrary lifecycle facts into the mirror.
        if (!RunLifecycleEventPublisher.IsAuthentic(evt))
        {
            logger.LogWarning(
                "Dropped a reserved-prefix event with a non-platform id. Type={EventType} Id={EventId}",
                evt.EventType, evt.EventId);
            return Task.CompletedTask;
        }
        return events.SaveAsync(new CoreEventRecord
        {
            Id = evt.EventId,
            EventType = evt.EventType,
            WorkflowType = evt.WorkflowType,
            WorkItemId = evt.WorkItemId,
            SourceRunId = evt.WorkflowInstanceId == Guid.Empty ? null : evt.WorkflowInstanceId,
            PayloadJson = evt.PayloadJson,
            CreatedUtc = evt.TimestampUtc
        }, ct);
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            do
            {
                await SweepOnceAsync(ct);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Service stop.
        }
    }

    /// <summary>Deletes events older than the retention window. Public for tests.</summary>
    public async Task SweepOnceAsync(CancellationToken ct = default)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.Value.EventRetentionDays);
            var expired = (await events.ReadAsync(ct)).Where(e => e.CreatedUtc < cutoff).ToList();
            foreach (var record in expired)
                await events.RemoveAsync(record.Id, ct);
            if (expired.Count > 0)
                logger.LogInformation("Event retention sweep removed {Count} event(s).", expired.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Event retention sweep failed — retrying on the next interval.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
            await _lifetime.CancelAsync();
        if (_subscription is not null)
            await _subscription.DisposeAsync();
        if (_sweeper is not null)
            await _sweeper.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ContinueWith(_ => { });
    }

    public void Dispose() => _lifetime?.Dispose();
}

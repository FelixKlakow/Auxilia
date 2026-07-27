using Auxilia.Core.Api.Data;
using Auxilia.Messaging;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Persists each run's view items into the Core's own store (from the
/// <see cref="ViewDataMessage.ExchangeName"/> fanout, its own exclusive queue — the live
/// <see cref="RunStreamPublisher"/> gets its own copy). Capped per run
/// (<see cref="CoreApiSettings.MaxPersistedViewItemsPerRun"/>); items beyond the cap are dropped
/// and logged — large data belongs in the artifact store, never in views. Deterministic ids make
/// re-delivery an idempotent upsert.
/// </summary>
public sealed class RunViewTrackingService(
    IMessageBusClient bus,
    IDataAccess<CoreRunViewRecord> views,
    IOptions<CoreApiSettings> settings,
    TimeProvider clock,
    ILogger<RunViewTrackingService> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareExchangeAsync(ViewDataMessage.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToExchangeAsync<ViewDataMessage>(
            ViewDataMessage.ExchangeName, HandleAsync, cancellationToken);
        logger.LogInformation("RunViewTrackingService listening on {Exchange}.", ViewDataMessage.ExchangeName);
    }

    private async Task HandleAsync(ViewDataMessage message, CancellationToken ct)
    {
        var stored = (await views.ReadAsync(ct)).Count(v => v.RunId == message.WorkflowInstanceId);
        if (stored >= settings.Value.MaxPersistedViewItemsPerRun)
        {
            logger.LogWarning(
                "Run {RunId} exceeded the persisted view-item cap ({Cap}); dropping view '{View}' seq {Sequence}.",
                message.WorkflowInstanceId, settings.Value.MaxPersistedViewItemsPerRun,
                message.ViewName, message.Sequence);
            return;
        }

        await views.SaveAsync(new CoreRunViewRecord
        {
            Id = CoreRunViewRecord.IdFor(message.WorkflowInstanceId, message.ViewName, message.Sequence),
            RunId = message.WorkflowInstanceId,
            ViewName = message.ViewName,
            Sequence = message.Sequence,
            PayloadJson = message.PayloadJson,
            TimestampUtc = clock.GetUtcNow()
        }, ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

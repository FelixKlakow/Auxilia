using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Persists view items for views whose declared lifecycle includes Persisted
/// (ARCHITECTURE §15), so the dashboard replays finished runs through the identical
/// rendering path. Live-only views are never stored. A per-view item cap bounds growth.
/// </summary>
public sealed class ViewDataHandler(
    IMessageBusClient messageBus,
    IDataAccess<ViewDataRecord> viewData,
    WorkflowInstanceRegistry instanceRegistry,
    TimeProvider timeProvider,
    IOptions<WorkflowDispatcherSettings> settings,
    ILogger<ViewDataHandler> logger)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await messageBus.DeclareExchangeAsync(ViewDataMessage.ExchangeName, ct);
        _subscription = await messageBus.SubscribeToExchangeAsync<ViewDataMessage>(
            ViewDataMessage.ExchangeName, HandleAsync, ct);

        logger.LogInformation("ViewDataHandler started — listening on {Exchange}.",
            ViewDataMessage.ExchangeName);
    }

    private async Task HandleAsync(ViewDataMessage message, CancellationToken ct)
    {
        var record = await instanceRegistry.GetAsync(message.WorkflowInstanceId, ct);
        if (record?.ViewsJson is null)
            return;

        var views = JsonSerializer.Deserialize<List<ViewDescriptor>>(record.ViewsJson) ?? [];
        var descriptor = views.FirstOrDefault(v => v.Name == message.ViewName);
        if (descriptor is null)
        {
            logger.LogWarning(
                "View data for undeclared view '{ViewName}' from instance {InstanceId} — dropping.",
                message.ViewName, message.WorkflowInstanceId);
            return;
        }

        if (descriptor.Lifecycle == ViewLifecycle.Live)
            return;

        if (message.Sequence > settings.Value.MaxViewItemsPerView)
        {
            logger.LogWarning(
                "View '{ViewName}' of instance {InstanceId} exceeded the per-view item cap ({Cap}) — dropping item {Sequence}.",
                message.ViewName, message.WorkflowInstanceId,
                settings.Value.MaxViewItemsPerView, message.Sequence);
            return;
        }

        await viewData.SaveAsync(new ViewDataRecord
        {
            Id = ViewDataRecord.IdFor(message.WorkflowInstanceId, message.ViewName, message.Sequence),
            WorkflowInstanceId = message.WorkflowInstanceId,
            ViewName = message.ViewName,
            Sequence = message.Sequence,
            PayloadJson = message.PayloadJson,
            TimestampUtc = timeProvider.GetUtcNow()
        }, ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

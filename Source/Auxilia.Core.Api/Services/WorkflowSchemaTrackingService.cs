using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Keeps registered workflow types' schemas fresh from the runner's schema announcements.
/// Subscribes to the <see cref="WorkflowSchemaPublished.ExchangeName"/> fanout (its own exclusive
/// queue) and updates the registry record of the announced type — <em>registered types only</em>:
/// a runtime announcement never creates a catalog entry, because registration (the deploy-time
/// trust act) is the sole way into the registry.
/// </summary>
public sealed class WorkflowSchemaTrackingService(
    IMessageBusClient bus,
    WorkflowTypeRegistryService registry,
    ILogger<WorkflowSchemaTrackingService> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareExchangeAsync(WorkflowSchemaPublished.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToExchangeAsync<WorkflowSchemaPublished>(
            WorkflowSchemaPublished.ExchangeName, HandleAsync, cancellationToken);
        logger.LogInformation(
            "WorkflowSchemaTrackingService listening on {Exchange}.", WorkflowSchemaPublished.ExchangeName);
    }

    private async Task HandleAsync(WorkflowSchemaPublished message, CancellationToken ct)
    {
        var updated = await registry.UpdateSchemaAsync(
            message.WorkflowType, JsonSerializer.Serialize(message.Schema), ct);
        if (!updated)
            logger.LogDebug(
                "Ignored schema announcement for unregistered workflow type {WorkflowType}.",
                message.WorkflowType);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

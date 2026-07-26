using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Messaging;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Builds the Core's catalog of registered workflow types from the runner's schema registrations.
/// Subscribes to the <see cref="WorkflowSchemaPublished.ExchangeName"/> fanout (its own exclusive
/// queue) and upserts a <see cref="CoreWorkflowSchemaRecord"/> per type. Mirrors
/// <see cref="RunTrackingService"/> — the Core never reads the runner's database.
/// </summary>
public sealed class WorkflowSchemaTrackingService(
    IMessageBusClient bus,
    IDataAccess<CoreWorkflowSchemaRecord> schemas,
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
        await schemas.SaveAsync(new CoreWorkflowSchemaRecord
        {
            Id = CoreWorkflowSchemaRecord.IdFor(message.WorkflowType),
            WorkflowType = message.WorkflowType,
            SchemaJson = JsonSerializer.Serialize(message.Schema),
            UpdatedUtc = message.TimestampUtc
        }, ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

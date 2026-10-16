using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Ingests the audit records other platform services (runners) forward, into the Core's
/// central, chained audit log — one trail for every plane. A shared queue: with N Core nodes
/// each record is ingested once; re-delivery is idempotent per record id. The message type is
/// platform-only, so a workload can never plant an entry.
/// </summary>
public sealed class AuditIngestionService(
    IMessageBusClient bus,
    AuditLog auditLog,
    ILogger<AuditIngestionService> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareExchangeAsync(AuditEntryMessage.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToExchangeSharedAsync<AuditEntryMessage>(
            AuditEntryMessage.ExchangeName, "core-api.audit-ingestion", HandleAsync, cancellationToken);
        logger.LogInformation("AuditIngestionService listening on {Exchange}.", AuditEntryMessage.ExchangeName);
    }

    private Task HandleAsync(AuditEntryMessage entry, CancellationToken ct)
        => auditLog.AppendForwardedAsync(new AuditRecord
        {
            Id = entry.RecordId,
            TimestampUtc = entry.TimestampUtc,
            Actor = entry.Actor,
            Action = entry.Action,
            Subject = entry.Subject,
            Outcome = entry.Outcome,
            DetailJson = entry.DetailJson
        }, entry.Source, ct);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

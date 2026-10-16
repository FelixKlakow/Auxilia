using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Forwards every audit record this runner writes to the Core's central audit log (the runner
/// keeps its own copy). A failed forward is logged, never thrown — auditing must not fail the
/// action it records; the local record and its chain remain.
/// </summary>
public sealed class BusAuditRelay(IMessageBusClient bus, CoreRunnerInfo runner, ILogger<BusAuditRelay> logger) : IAuditRelay
{
    private bool _declared;

    public async Task RelayAsync(AuditRecord record, CancellationToken ct)
    {
        try
        {
            if (!_declared)
            {
                await bus.DeclareExchangeAsync(AuditEntryMessage.ExchangeName, ct);
                _declared = true;
            }
            await bus.PublishToExchangeAsync(AuditEntryMessage.ExchangeName, new AuditEntryMessage(
                record.Id, record.TimestampUtc, record.Actor, record.Action, record.Subject, record.Outcome,
                record.DetailJson, $"core-runner:{runner.ServiceId:D}"), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Forwarding audit record {Action} to the Core failed; the runner keeps its local copy.",
                record.Action);
        }
    }
}

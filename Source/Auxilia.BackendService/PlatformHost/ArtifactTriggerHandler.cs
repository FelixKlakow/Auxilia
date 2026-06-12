using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.PlatformHost;

/// <summary>
/// Artifact-completion trigger (ARCHITECTURE §6): consumes <see cref="ArtifactPersistedEvent"/>
/// and dispatches configured follow-up workflows with the artifact reference in their context —
/// chaining without coupling. The chained run carries the trigger's run-as principal, so it
/// passes the same policy checks as a manual dispatch.
/// </summary>
public sealed class ArtifactTriggerHandler(
    IMessageBusClient messageBus,
    IDataAccess<ArtifactTriggerRecord> triggers,
    AuditLog auditLog,
    IOptions<PlatformHostSettings> settings,
    ILogger<ArtifactTriggerHandler> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await messageBus.DeclareExchangeAsync(ArtifactPersistedEvent.ExchangeName, cancellationToken);
        _subscription = await messageBus.SubscribeToExchangeAsync<ArtifactPersistedEvent>(
            ArtifactPersistedEvent.ExchangeName, HandleAsync, cancellationToken);

        logger.LogInformation("ArtifactTriggerHandler started — listening on {Exchange}.",
            ArtifactPersistedEvent.ExchangeName);
    }

    private async Task HandleAsync(ArtifactPersistedEvent persisted, CancellationToken ct)
    {
        var query = await triggers.ReadAsync(ct);
        var matching = query
            .Where(t => t.Enabled && t.ArtifactType == persisted.ArtifactType)
            .ToList();

        foreach (var trigger in matching)
        {
            var command = new RunWorkflowCommand(
                Guid.NewGuid(), trigger.WorkflowType, trigger.WorkflowPackageUri,
                new Dictionary<string, string>
                {
                    ["ArtifactId"] = persisted.ArtifactId.ToString("D"),
                    ["ArtifactType"] = persisted.ArtifactType,
                    ["WorkItemId"] = persisted.WorkItemId
                },
                trigger.RunAsPrincipalId);

            await messageBus.PublishAsync(settings.Value.CommandQueueName, command, ct);
            await auditLog.AppendAsync("backend-service", "trigger.artifact-dispatch",
                persisted.ArtifactId.ToString(), command.CommandId.ToString(), ct: ct);

            logger.LogInformation(
                "Artifact-completion trigger dispatched. Artifact={ArtifactType} v{Version} → Workflow={WorkflowType} Command={CommandId}",
                persisted.ArtifactType, persisted.Version, trigger.WorkflowType, command.CommandId);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

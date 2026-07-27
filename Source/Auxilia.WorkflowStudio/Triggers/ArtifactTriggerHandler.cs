using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.WorkflowStudio.Triggers;

/// <summary>
/// Artifact-completion trigger (ARCHITECTURE §6): subscribes to <see cref="ArtifactPersistedEvent"/>
/// on the bus and dispatches configured follow-up workflows — through the Core Run API — with the
/// artifact reference in their context, chaining workflows without coupling them. The inbound
/// artifact-event subscription is the Studio's one read-only bus dependency; the outbound dispatch
/// goes via <see cref="ICoreClient"/> (the Studio never publishes raw run commands). The chained run
/// carries the trigger's run-as principal, so it passes the same policy checks as a manual dispatch.
/// </summary>
public sealed class ArtifactTriggerHandler(
    IMessageBusClient messageBus,
    ICoreClient core,
    IDataAccess<ArtifactTriggerRecord> triggers,
    AuditLog auditLog,
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

    internal async Task HandleAsync(ArtifactPersistedEvent persisted, CancellationToken ct)
    {
        var query = await triggers.ReadAsync(ct);
        var matching = query
            .Where(t => t.Enabled && t.ArtifactType == persisted.ArtifactType)
            .ToList();

        foreach (var trigger in matching)
        {
            var context = new Dictionary<string, string>
            {
                ["ArtifactId"] = persisted.ArtifactId.ToString("D"),
                ["ArtifactType"] = persisted.ArtifactType,
                ["WorkItemId"] = persisted.WorkItemId
            };

            // Configuration-wired triggers (#20) dispatch the stored configuration by id, on behalf of
            // the run-as principal; inline triggers dispatch the registered workflow type directly. The Core
            // is the authorization authority and evaluates the run-as principal.
            var accepted = trigger.WorkflowConfigurationId is { } configId
                ? await core.RunConfigurationAsync(
                    configId, onBehalfOf: trigger.RunAsPrincipalId, context: context, ct)
                : await core.RunAsync(
                    new RunRequest(trigger.WorkflowType, context,
                        RequestedBy: trigger.RunAsPrincipalId), ct);

            await auditLog.AppendAsync("workflow-studio", "trigger.artifact-dispatch",
                persisted.ArtifactId.ToString(), accepted.CommandId.ToString(), ct: ct);

            logger.LogInformation(
                "Artifact-completion trigger dispatched. Artifact={ArtifactType} v{Version} → Workflow={WorkflowType} RunId={RunId}",
                persisted.ArtifactType, persisted.Version, trigger.WorkflowType, accepted.RunId);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

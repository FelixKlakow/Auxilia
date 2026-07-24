using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Just-in-time per-slot credential delivery (ARCHITECTURE §4/§7): a workflow receives a
/// slot's configuration only when it first activates the slot (or its credential expired),
/// authenticated by the instance token, encrypted for the instance's ephemeral key, and
/// answered only on the instance's exclusive response queue. Every activation is audited.
/// </summary>
public sealed class SlotActivationHandler(
    IMessageBusClient messageBus,
    ConfigurationResolver configResolver,
    WorkflowInstanceRegistry instanceRegistry,
    WorkflowInstanceTokenRegistry tokenRegistry,
    AuditLog auditLog,
    TimeProvider timeProvider,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
    ILogger<SlotActivationHandler> logger)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var queueName = dispatcherSettings.Value.SlotActivationQueueName;
        await messageBus.DeclareQueueAsync(queueName, ct);
        _subscription = await messageBus.SubscribeAsync<SlotActivationRequest>(queueName, HandleAsync, ct);

        logger.LogInformation("SlotActivationHandler started — listening on {QueueName}.", queueName);
    }

    private async Task HandleAsync(SlotActivationRequest request, CancellationToken ct)
    {
        var responseTopic = WorkflowQueues.ResponseQueueFor(request.WorkflowInstanceId);

        if (dispatcherSettings.Value.RequireInstanceToken &&
            !tokenRegistry.Validate(request.WorkflowInstanceId, request.InstanceToken))
        {
            logger.LogWarning(
                "Rejected SlotActivationRequest with missing or invalid instance token. InstanceId={InstanceId} Slot={SlotName}",
                request.WorkflowInstanceId, request.SlotName);
            await auditLog.AppendAsync(
                "steering-instance", "workflow.slot-activation.rejected",
                request.WorkflowInstanceId.ToString(), "invalid-instance-token", ct: ct);
            return;
        }

        var instance = await instanceRegistry.GetAsync(request.WorkflowInstanceId, ct);
        if (instance is null)
        {
            logger.LogWarning(
                "SlotActivationRequest for unregistered instance {InstanceId} — rejecting.",
                request.WorkflowInstanceId);
            await messageBus.PublishAsync(responseTopic, new SlotActivationResponse(
                request.WorkflowInstanceId, request.SlotName, false,
                "Instance is not registered", null), ct);
            return;
        }

        // Source selection (#18): an instance dispatched from a named configuration is served
        // from that configuration's slot bindings; every other instance keeps the global
        // (workflow type, slot) table.
        var (success, error, slot) = instance.WorkflowConfigurationId is { } configurationId
            ? await configResolver.ResolveConfigurationSlotAsync(
                configurationId, request.SlotName, request.PublicKey, ct)
            : await configResolver.ResolveSlotAsync(
                instance.WorkflowType, request.SlotName, request.PublicKey, ct);
        if (!success)
        {
            await auditLog.AppendAsync(
                "steering-instance", "workflow.slot-activation.rejected",
                request.WorkflowInstanceId.ToString(), error ?? "resolution-failed", ct: ct);
            await messageBus.PublishAsync(responseTopic, new SlotActivationResponse(
                request.WorkflowInstanceId, request.SlotName, false, error, null), ct);
            return;
        }

        var expiresUtc = timeProvider.GetUtcNow() + dispatcherSettings.Value.SlotCredentialLifetime;
        await auditLog.AppendAsync(
            "steering-instance", "workflow.slot-activated",
            request.WorkflowInstanceId.ToString(), request.SlotName, ct: ct);
        await messageBus.PublishAsync(responseTopic, new SlotActivationResponse(
            request.WorkflowInstanceId, request.SlotName, true, null, slot, expiresUtc), ct);

        logger.LogInformation(
            "Slot activated. InstanceId={InstanceId} Slot={SlotName} ExpiresUtc={ExpiresUtc:O}",
            request.WorkflowInstanceId, request.SlotName, expiresUtc);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

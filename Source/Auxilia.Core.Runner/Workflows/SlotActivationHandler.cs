using System.Text.Json;
using Auxilia.Core.Contracts;
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
    ICoreCredentialClient coreCredentialClient,
    WorkflowInstanceRegistry instanceRegistry,
    WorkflowInstanceTokenRegistry tokenRegistry,
    AuditLog auditLog,
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

        // A run always carries a run-scoped resolution token: the Core resolves the connector and
        // encrypts it for this instance, so plaintext never enters the runner — we only relay the
        // ciphertext. There is no local credential store.
        var command = TryParseCommand(instance.DispatchCommandJson);
        if (command?.ResolutionToken is not { Length: > 0 } resolutionToken)
        {
            await auditLog.AppendAsync(
                "steering-instance", "workflow.slot-activation.rejected",
                request.WorkflowInstanceId.ToString(), "no-resolution-token", ct: ct);
            await messageBus.PublishAsync(responseTopic, new SlotActivationResponse(
                request.WorkflowInstanceId, request.SlotName, false,
                "run has no credential resolution context", null), ct);
            return;
        }

        var resolved = await coreCredentialClient.ResolveAsync(
            command.CommandId, resolutionToken, request.SlotName, request.PublicKey, ct);
        if (resolved is null)
        {
            await auditLog.AppendAsync(
                "steering-instance", "workflow.slot-activation.rejected",
                request.WorkflowInstanceId.ToString(), "core-resolution-failed", ct: ct);
            await messageBus.PublishAsync(responseTopic, new SlotActivationResponse(
                request.WorkflowInstanceId, request.SlotName, false,
                "credential resolution failed in the Core", null), ct);
            return;
        }

        var slot = new EncryptedSlotConfiguration(resolved.ProviderType, resolved.EncryptedSettings);
        await auditLog.AppendAsync(
            "steering-instance", "workflow.slot-activated",
            request.WorkflowInstanceId.ToString(), request.SlotName, ct: ct);
        await messageBus.PublishAsync(responseTopic, new SlotActivationResponse(
            request.WorkflowInstanceId, request.SlotName, true, null, slot, resolved.ExpiresUtc), ct);

        logger.LogInformation(
            "Slot activated. InstanceId={InstanceId} Slot={SlotName} ExpiresUtc={ExpiresUtc:O}",
            request.WorkflowInstanceId, request.SlotName, resolved.ExpiresUtc);
    }

    private static RunWorkflowCommand? TryParseCommand(string? dispatchCommandJson)
    {
        if (string.IsNullOrEmpty(dispatchCommandJson))
            return null;
        try { return JsonSerializer.Deserialize<RunWorkflowCommand>(dispatchCommandJson); }
        catch (JsonException) { return null; }
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

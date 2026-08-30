using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Protection;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Drain-and-replace (ARCHITECTURE §6): republishes a drained long-living instance's stored
/// dispatch command with a fresh CommandId so a replacement boots with the updated
/// configuration. Shared by the graceful path (terminal <c>WorkflowStateMessage</c> in
/// <see cref="WorkflowStateHandler"/>) and the drain-crash path (container died while
/// Draining without ever publishing a terminal message, in
/// <see cref="WorkflowDispatcher.HandleContainerExitAsync"/>).
/// </summary>
internal static class DrainReplacement
{
    public static async Task PublishAsync(
        IMessageBusClient messageBus,
        ISettingsProtector settingsProtector,
        AuditLog auditLog,
        string commandQueueName,
        Guid drainedInstanceId,
        string? dispatchCommandJson,
        ILogger logger,
        CancellationToken ct)
    {
        if (dispatchCommandJson is not { Length: > 0 } json)
            return;
        RunWorkflowCommand? original;
        try
        {
            original = JsonSerializer.Deserialize<RunWorkflowCommand>(json);
        }
        catch (JsonException)
        {
            original = null;
        }
        if (original is null)
            return;

        // The stored command carries its resolution token protected at rest — the replacement
        // must ride the bus exactly like the original dispatch did (the dispatcher re-protects
        // it when persisting the replacement's own record).
        var replacement = DispatchCommandProtection.Unprotect(original, settingsProtector, logger)
            with { CommandId = Guid.NewGuid() };
        await messageBus.PublishAsync(commandQueueName, replacement, ct);
        await auditLog.AppendAsync(
            "steering-instance", "workflow.drain-replaced",
            drainedInstanceId.ToString(), replacement.CommandId.ToString(), ct: ct);
        logger.LogInformation(
            "Drained instance {InstanceId} replaced — new dispatch {CommandId}.",
            drainedInstanceId, replacement.CommandId);
    }
}

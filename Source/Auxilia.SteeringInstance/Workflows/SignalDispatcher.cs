using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class SignalDispatcher(
    IMessageBusClient messageBus,
    SignalHandlerStore handlerStore,
    WorkflowInstanceRegistry instanceRegistry,
    ILogger<SignalDispatcher> logger)
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await messageBus.DeclareQueueAsync("workflow.signals", cancellationToken);
        await messageBus.SubscribeAsync<WorkflowSignalMessage>("workflow.signals", HandleAsync, cancellationToken);
    }

    private async Task HandleAsync(WorkflowSignalMessage message, CancellationToken ct)
    {
        if (!instanceRegistry.TryGetWorkflowType(message.WorkflowInstanceId, out var typeName))
        {
            logger.LogWarning("No registered workflow type for instance {InstanceId}", message.WorkflowInstanceId);
            return;
        }

        var handlers = handlerStore.GetHandlers(typeName!);
        var stored = handlers.FirstOrDefault(h => h.SignalName == message.SignalName);
        if (stored is null)
        {
            logger.LogWarning(
                "No signal handler configured for signal '{SignalName}' on workflow type '{WorkflowType}'",
                message.SignalName, typeName);
            return;
        }

        switch (stored.HandlerDescriptor)
        {
            case InvokeWorkflowSignalHandler invoke:
                await messageBus.PublishAsync(
                    "workflow.signal-routes",
                    new WorkflowSignalRouteMessage(invoke.TargetWorkflowName, message.SignalName, message.PayloadJson),
                    ct);
                break;

            case NotifySignalHandler:
                await messageBus.PublishAsync(
                    "workflow.notifications",
                    new WorkflowNotificationMessage(message.WorkflowInstanceId, message.SignalName, message.PayloadJson),
                    ct);
                break;

            case NullSignalHandler:
                logger.LogDebug(
                    "Signal '{SignalName}' for instance {InstanceId} matched NullSignalHandler — swallowing.",
                    message.SignalName, message.WorkflowInstanceId);
                break;
        }
    }
}

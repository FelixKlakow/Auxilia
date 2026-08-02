using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows;

public sealed class DefaultSignalEmitter(IMessageBusClient messageBus, WorkflowInstanceContext context)
    : ISignalEmitter
{
    public Task EmitAsync<TPayload>(string signalName, TPayload payload,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        var message = new WorkflowSignalMessage(context.InstanceId, signalName, payloadJson);
        return messageBus.PublishAsync("workflow.signals", message, cancellationToken);
    }
}

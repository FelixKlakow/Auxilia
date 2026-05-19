using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SteeringInstance.Workflows.Storage;

public sealed record StoredSignalHandlerConfiguration(
    string SignalName,
    ISignalHandlerDescriptor HandlerDescriptor);

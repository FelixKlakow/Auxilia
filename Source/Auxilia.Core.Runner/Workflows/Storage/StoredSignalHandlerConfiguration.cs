using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Runner.Workflows.Storage;

public sealed record StoredSignalHandlerConfiguration(
    string SignalName,
    ISignalHandlerDescriptor HandlerDescriptor);

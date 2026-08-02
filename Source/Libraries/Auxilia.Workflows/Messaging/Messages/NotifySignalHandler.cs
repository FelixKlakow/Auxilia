namespace Auxilia.Workflows.Messaging.Messages;

public sealed record NotifySignalHandler(
    string Channel,
    IReadOnlyDictionary<string, string> Parameters) : ISignalHandlerDescriptor;

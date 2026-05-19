namespace Auxilia.Workflows.Messaging.Messages;

public sealed record InvokeWorkflowSignalHandler(string TargetWorkflowName) : ISignalHandlerDescriptor;

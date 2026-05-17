using Auxilia.Messaging;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows;

public interface IWorkflowRunContext
{
    IMessageBusClient MessageBus { get; }
    IProcessExitService ExitService { get; }
    ILogger Logger { get; }
}

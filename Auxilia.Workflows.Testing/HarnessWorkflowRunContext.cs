using Auxilia.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Workflows.Testing;

internal sealed class HarnessWorkflowRunContext : IWorkflowRunContext
{
    public HarnessWorkflowRunContext(InProcessMessageBus messageBus)
    {
        MessageBus = messageBus;
    }

    public IMessageBusClient MessageBus { get; }
    public IProcessExitService ExitService { get; } = new NoOpProcessExitService();
    public ILogger Logger => NullLogger.Instance;

    private sealed class NoOpProcessExitService : IProcessExitService
    {
        public void Exit(int exitCode) { }
    }
}

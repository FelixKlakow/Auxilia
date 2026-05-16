using Auxilia.Messaging;
using Auxilia.Workflows.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows;

public interface IWorkflowBuilder
{
    IWorkflowBuilder Requires<T>(string name, T capabilities, string? description = null)
        where T : ICapability;

    IWorkflowBuilder RequiresEnvironment(Action<IEnvironmentBuilder> configure);

    IWorkflowBuilder WithMetadata(Action<WorkflowMetadata> configure);

    Task Run(string[] args);

    Task Run(string[] args, IMessageBusClient messageBus, IServiceCollection services,
        ILogger? logger = null, IProcessExitService? exitService = null);
}

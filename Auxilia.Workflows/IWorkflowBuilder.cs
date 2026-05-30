using Auxilia.Workflows.Capabilities;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows;

public interface IWorkflowBuilder
{
    IWorkflowBuilder Requires<TService>(string name, ICapability capabilities, string? description = null);

    IWorkflowBuilder RequiresEnvironment(Action<IEnvironmentBuilder> configure);

    IWorkflowBuilder WithMetadata(Action<WorkflowMetadata> configure);

    IWorkflowBuilder DeclaresOutput(string name, string relativePath, string? description = null);

    IWorkflowBuilder DeclaresSignal<TPayload>(string name, string? description = null);

    IWorkflowBuilder ConfigureServices(Action<IServiceCollection> configure);

    IWorkflowBuilder WithApplication(Func<IServiceProvider, CancellationToken, Task> run);

    Task Run(string[] args);

    Task Run(string[] args, IWorkflowRunContext context);

    Task RunAsync(string[] args, IWorkflowRunContext context);
}

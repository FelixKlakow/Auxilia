using Auxilia.Workflows.Capabilities;

namespace Auxilia.Workflows;

public interface IWorkflowBuilder
{
    IWorkflowBuilder Requires<T>(string name, T capabilities, string? description = null)
        where T : ICapability;

    IWorkflowBuilder RequiresEnvironment(Action<IEnvironmentBuilder> configure);

    IWorkflowBuilder WithMetadata(Action<WorkflowMetadata> configure);

    IWorkflowBuilder DeclaresOutput(string name, string relativePath, string? description = null);

    Task Run(string[] args);

    Task Run(string[] args, IWorkflowRunContext context);

    Task RunAsync(string[] args, IWorkflowRunContext context);
}

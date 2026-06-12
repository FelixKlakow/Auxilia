using Auxilia.Workflows.Capabilities;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows;

public interface IWorkflowBuilder
{
    IWorkflowBuilder Requires<TService>(string name, ICapability capabilities, string? description = null);

    IWorkflowBuilder RequiresEnvironment(Action<IEnvironmentBuilder> configure);

    IWorkflowBuilder WithMetadata(Action<WorkflowMetadata> configure);

    /// <summary>
    /// Declares the workflow's lifetime. Long-living workflows receive a
    /// <see cref="WorkflowDrainSignal"/> via DI and must honour it; deployment additionally
    /// requires operator approval on the Steering Instance.
    /// </summary>
    IWorkflowBuilder WithLifetime(WorkflowLifetime lifetime);

    IWorkflowBuilder DeclaresOutput(string name, string relativePath, string? description = null);

    IWorkflowBuilder DeclaresSignal<TPayload>(string name, string? description = null);

    /// <summary>
    /// Declares a named, schema-declared view (ARCHITECTURE §15). Items published via
    /// <see cref="Views.IViewPublisher"/> must conform to <typeparamref name="TItem"/>.
    /// </summary>
    IWorkflowBuilder DeclaresView<TItem>(
        string name, Views.ViewRendering rendering, Views.ViewLifecycle lifecycle);

    IWorkflowBuilder ConfigureServices(Action<IServiceCollection> configure);

    IWorkflowBuilder WithApplication(Func<IServiceProvider, CancellationToken, Task> run);

    Task Run(string[] args);

    Task Run(string[] args, IWorkflowRunContext context);

    Task RunAsync(string[] args, IWorkflowRunContext context);

    WorkflowSchema BuildSchema();
}

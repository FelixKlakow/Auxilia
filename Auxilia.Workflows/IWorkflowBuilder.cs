using Auxilia.Workflows.Capabilities;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows;

public interface IWorkflowBuilder
{
    IWorkflowBuilder Requires<T>(string name, T capabilities, string? description = null)
        where T : ICapability;

    IWorkflowBuilder RequiresEnvironment(Action<IEnvironmentBuilder> configure);

    IWorkflowBuilder WithMetadata(Action<WorkflowMetadata> configure);

    IWorkflowBuilder DeclaresOutput(string name, string relativePath, string? description = null);

    /// <summary>
    /// Registers the business-logic body of the workflow.
    /// The delegate is executed after the Steering Instance handshake completes and the
    /// DI container is fully configured with resolved slot providers.
    /// </summary>
    /// <param name="body">
    /// Receives the fully-configured <see cref="IServiceProvider"/> (slot handlers wired in)
    /// and a <see cref="CancellationToken"/> tied to the host lifetime.
    /// </param>
    IWorkflowBuilder WithRunBody(Func<IServiceProvider, CancellationToken, Task> body);

    IWorkflowBuilder DeclaresSignal<TPayload>(string name, string? description = null);

    IWorkflowBuilder ConfigureServices(Action<IServiceCollection> configure);

    IWorkflowBuilder WithApplication(Func<IServiceProvider, Task> run);

    Task Run(string[] args);

    Task Run(string[] args, IWorkflowRunContext context);

    Task RunAsync(string[] args, IWorkflowRunContext context);
}

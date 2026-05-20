using Auxilia.ImplementationWorkflow.Branch;
using Auxilia.ImplementationWorkflow.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.ImplementationWorkflow;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddImplementationWorkflow(
        this IServiceCollection services,
        string? outputDirectory = null)
    {
        services.TryAddSingleton<ImplementationWorkflowConfiguration>(sp =>
        {
            var config = new ImplementationWorkflowConfiguration();
            return outputDirectory is not null
                ? config with { OutputDirectory = outputDirectory }
                : config;
        });

        services.TryAddSingleton<WorkItemTrigger>(_ =>
            throw new InvalidOperationException("WorkItemTrigger must be registered before calling AddImplementationWorkflow."));

        services.TryAddSingleton<BranchNamingService>();
        services.TryAddSingleton<InstructionsFileLocator>();
        services.TryAddSingleton<ContextAssembler>();
        services.TryAddSingleton<BranchSetupService>();
        services.TryAddSingleton<AgentOrchestrator>();
        services.TryAddSingleton<ReviewerOrchestrator>();
        services.TryAddSingleton<PullRequestService>();
        services.TryAddSingleton<WriteBackService>();
        services.TryAddSingleton<ImplementationSummaryWriter>();
        services.TryAddSingleton<CompletionSignalEmitter>();

        return services;
    }
}

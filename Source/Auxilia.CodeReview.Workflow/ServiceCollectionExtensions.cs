using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.PrimaryReview;
using Auxilia.CodeReview.Workflow.Verdicts;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodeReview.Workflow;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeReviewWorkflow(this IServiceCollection services)
    {
        // Phase 3 — Primary Review
        services.AddSingleton<IStagedFindingsStore, StagedFindingsStore>();
        services.AddSingleton<VerdictMap>();
        services.AddScoped<ContextCompactionService>();
        services.AddScoped<PrimaryReviewOrchestrator>();

        // Phase 4 — Two-Eyes Pass and Aggregation
        services.AddScoped<TwoEyesPassService>();
        services.AddScoped<FindingsAggregator>();

        // Phase 5 — Write-Back and Metrics
        services.AddScoped<WriteBackService>();
        services.AddScoped<CoverageMetricsProducer>();
        services.AddScoped<TerminalStatePublisher>();

        return services;
    }
}

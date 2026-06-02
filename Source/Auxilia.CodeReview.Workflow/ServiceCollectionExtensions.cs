using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.PrimaryReview;
using Auxilia.CodeReview.Workflow.Verdicts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Auxilia.CodeReview.Workflow;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeReviewWorkflow(this IServiceCollection services, string outputDirectory = "output")
    {
        // Configuration defaults (overridable by pre-registering before calling this method)
        services.TryAddScoped(_ => new TwoEyesConfiguration());
        services.TryAddScoped(_ => new WriteBackConfiguration());
        services.TryAdd(new ServiceDescriptor(typeof(WorkItemRetrievalFailureBehavior), _ => WorkItemRetrievalFailureBehavior.Ignore, ServiceLifetime.Scoped));
        services.TryAddScoped(_ => new CriticalityClassifier(Array.Empty<string>()));
        services.TryAddScoped<IOptions<ContextCompactionOptions>>(_ => Options.Create(new ContextCompactionOptions()));

        // Phase 2 — Context Assembly
        services.TryAddScoped<ContextAssembler>();

        // Phase 3 — Primary Review
        services.TryAddScoped<IStagedFindingsStore, StagedFindingsStore>();
        services.TryAddScoped<VerdictMap>();
        services.TryAddScoped<ContextCompactionService>();
        services.TryAddScoped<PrimaryReviewOrchestrator>();

        // Phase 4 — Two-Eyes Pass and Aggregation
        services.TryAddScoped<TwoEyesPassService>();
        services.TryAddScoped<FindingsAggregator>();

        // Phase 5 — Write-Back and Metrics
        services.TryAddScoped<WriteBackService>();
        services.TryAddScoped<CoverageMetricsProducer>();
        services.TryAddScoped<TerminalStatePublisher>(_ => new TerminalStatePublisher(outputDirectory));

        return services;
    }
}

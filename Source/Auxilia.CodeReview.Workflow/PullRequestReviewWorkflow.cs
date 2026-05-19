using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.PrimaryReview;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodeReview.Workflow;

public static class PullRequestReviewWorkflow
{
    public static Task Main(string[] args) =>
        WorkflowBuilder.Create("pull-request-code-review")
            .RequiresSourceControl("repository",
                new SourceControlCapabilities { RequiredPermissions = [Permission.Read] })
            .RequiresPullRequestAccess("pull-request",
                new PullRequestAccessCapabilities { RequiredPermissions = ["ReadWrite"] })
            .RequiresTaskSource("work-items",
                new TaskSourceCapabilities { SupportedItemTypes = [ItemType.UserStory, ItemType.Bug, ItemType.Feature, ItemType.Epic] })
            .RequiresAiAgent("primary-reviewer",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] })
            .RequiresAiAgent("secondary-reviewer",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] })
            .DeclaresOutput("code-review-result", "output/code-review-result.json", "Structured code review findings")
            .ConfigureServices(services => services.AddCodeReviewWorkflow())
            .WithApplication(ExecuteWorkflowAsync)
            .Run(args);

    public static Task RunAsync() => Main(["--test-harness"]);

    private static async Task ExecuteWorkflowAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var assembler = sp.GetRequiredService<ContextAssembler>();
        var context = await assembler.AssembleAsync(new PullRequestReference("pr", "main", "head"));

        var primaryOrchestrator = sp.GetRequiredService<PrimaryReviewOrchestrator>();
        await primaryOrchestrator.RunAsync(context);

        var store = sp.GetRequiredService<IStagedFindingsStore>();
        var aggregator = sp.GetRequiredService<FindingsAggregator>();
        var result = await aggregator.AggregateAsync(store, context);

        var writeBack = sp.GetRequiredService<WriteBackService>();
        await writeBack.WriteBackAsync(result, context.LinkedWorkItems.Select(wi => wi.Id).ToList());

        var metricsProducer = sp.GetRequiredService<CoverageMetricsProducer>();
        var metrics = metricsProducer.Produce(context, result);

        var publisher = sp.GetRequiredService<TerminalStatePublisher>();
        await publisher.PublishAsync(result, metrics);

        if (metrics.FailedFileCount > 0)
            throw new InvalidOperationException(
                $"{metrics.FailedFileCount} critical file(s) received a Failed verdict and could not be reviewed. Workflow aborted.");
    }
}

using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.PrimaryReview;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodeReview.Workflow;

public static class PullRequestReviewWorkflow
{
    public static Task Main(string[] args) =>
        WorkflowBuilder.Create("pull-request-code-review")
            .RequiresSourceControl("repository",
                new SourceControlCapabilities { RequiredPermissions = [Permission.Read] })
            .RequiresPullRequestAccess("pull-request",
                new PullRequestAccessCapabilities { RequiredPermissions = [PullRequestPermission.Read, PullRequestPermission.Write] })
            // The workflow only reads items and posts comments (IWorkItemAccess) — declaring
            // the narrower contract lets matching providers (e.g. a mailbox) fill the slot.
            .RequiresWorkItems("work-items",
                new TaskSourceCapabilities { SupportedItemTypes = [ItemType.UserStory, ItemType.Bug, ItemType.Feature, ItemType.Epic] })
            .RequiresAiAgent("primary-reviewer",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] })
            .RequiresAiAgent("secondary-reviewer",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] })
            .DeclaresView<StagedFinding>("review-findings", ViewRendering.Table, ViewLifecycle.LiveAndPersisted)
            .DeclaresView<ReviewProgressEntry>("progress", ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresView<AgentChatEntry>("agent-conversation", ViewRendering.Custom, ViewLifecycle.LiveAndPersisted,
                AgentChatEntry.RendererKey)
            .DeclaresOutput("code-review-result", "code-review-result.json", "Structured code review findings")
            // The trigger lives outside the workflow (wired per configuration) — this
            // declaration tells configurators what kind of trigger the workflow expects.
            .DeclaresTrigger(TriggerDeclaration.Mailbox,
                "Runs once per incoming work-item mail; subject and body become the review context.")
            .ConfigureServices(services => services.AddCodeReviewWorkflow())
            .WithApplication(ExecuteWorkflowAsync)
            .Run(args);

    public static Task RunAsync() => Main(["--test-harness"]);

    private static async Task ExecuteWorkflowAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Optional so DI setups without a platform connection (older test fixtures) still run.
        var views = sp.GetService<IViewPublisher>();

        await PublishProgressAsync(views, "context-assembly", "started", cancellationToken);
        var assembler = sp.GetRequiredService<ContextAssembler>();
        var context = await assembler.AssembleAsync(cancellationToken);
        await PublishProgressAsync(views, "context-assembly", "completed", cancellationToken);

        await PublishProgressAsync(views, "primary-review", "started", cancellationToken);
        var primaryOrchestrator = sp.GetRequiredService<PrimaryReviewOrchestrator>();
        await primaryOrchestrator.RunAsync(context, cancellationToken);
        await PublishProgressAsync(views, "primary-review", "completed", cancellationToken);

        await PublishProgressAsync(views, "aggregation", "started", cancellationToken);
        var store = sp.GetRequiredService<IStagedFindingsStore>();
        var aggregator = sp.GetRequiredService<FindingsAggregator>();
        var result = await aggregator.AggregateAsync(store, context, cancellationToken);
        await PublishProgressAsync(views, "aggregation", "completed", cancellationToken);

        await PublishProgressAsync(views, "write-back", "started", cancellationToken);
        var writeBack = sp.GetRequiredService<WriteBackService>();
        await writeBack.WriteBackAsync(result, context.LinkedWorkItems.Select(wi => wi.Id).ToList());
        await PublishProgressAsync(views, "write-back", "completed", cancellationToken);

        await PublishProgressAsync(views, "publish-result", "started", cancellationToken);
        var metricsProducer = sp.GetRequiredService<CoverageMetricsProducer>();
        var metrics = metricsProducer.Produce(context, result);

        var publisher = sp.GetRequiredService<TerminalStatePublisher>();
        await publisher.PublishAsync(result, metrics);
        await PublishProgressAsync(views, "publish-result", "completed", cancellationToken);

        if (metrics.FailedFileCount > 0)
            throw new InvalidOperationException(
                $"{metrics.FailedFileCount} critical file(s) received a Failed verdict and could not be reviewed. Workflow aborted.");
    }

    private static Task PublishProgressAsync(
        IViewPublisher? views, string phase, string message, CancellationToken cancellationToken)
        => views?.PublishAsync("progress", new ReviewProgressEntry(phase, message, DateTimeOffset.UtcNow), cancellationToken)
           ?? Task.CompletedTask;
}

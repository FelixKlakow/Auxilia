using Auxilia.ImplementationWorkflow.Branch;
using Auxilia.ImplementationWorkflow.Context;
using Auxilia.ImplementationWorkflow.Signals;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Capabilities;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.TestRunner;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow;

public static class ImplementationWorkflow
{
    public static Task Main(string[] args) =>
        WorkflowBuilder.Create("implementation-workflow")
            .RequiresSourceControl("repository",
                new SourceControlCapabilities { RequiredPermissions = [Permission.Read, Permission.Write] })
            .RequiresTaskSource("task-source",
                new TaskSourceCapabilities { SupportedItemTypes = [ItemType.UserStory, ItemType.Bug, ItemType.Feature] })
            .RequiresAiAgent("implementation-agent",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] })
            .RequiresAiAgent("reviewer-agent",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] })
            .RequiresTestRunner("test-runner", new TestRunnerCapabilities())
            .RequiresPullRequestAccess("pull-request",
                new PullRequestAccessCapabilities { RequiredPermissions = ["ReadWrite"] })
            .Requires("workflow-config", new NoCapabilities())
            .DeclaresOutput("implementation-summary", "output/implementation-summary.json", "Implementation run summary with branch, PR URL, and review notes")
            .DeclaresSignal<CompletedSignalPayload>("Completed", "Emitted when the workflow completes successfully")
            .DeclaresSignal<ReviewNotesFlaggedSignalPayload>("ReviewNotesFlagged", "Emitted when the reviewer flags issues")
            .DeclaresSignal<FailedSignalPayload>("Failed", "Emitted when the implementation agent fails")
            .WithApplication(ExecuteAsync)
            .Run(args);

    public static Task RunAsync() => Main(["--test-harness"]);

    private static async Task ExecuteAsync(IServiceProvider provider)
    {
        var contextAssembler = provider.GetRequiredService<ContextAssembler>();
        var branchSetup = provider.GetRequiredService<BranchSetupService>();
        var agentOrchestrator = provider.GetRequiredService<AgentOrchestrator>();
        var reviewerOrchestrator = provider.GetRequiredService<ReviewerOrchestrator>();
        var pullRequestService = provider.GetRequiredService<PullRequestService>();
        var writeBackService = provider.GetRequiredService<WriteBackService>();
        var summaryWriter = provider.GetRequiredService<ImplementationSummaryWriter>();
        var completionSignalEmitter = provider.GetRequiredService<CompletionSignalEmitter>();

        var context = await contextAssembler.AssembleAsync();
        await branchSetup.SetupAsync(context.BranchName);

        var agentResult = await agentOrchestrator.RunAsync(context);
        var reviewNotes = await reviewerOrchestrator.RunAsync(agentResult, context);
        var prUrl = await pullRequestService.OpenAsync(agentResult, context);

        await writeBackService.WriteAsync(context, prUrl);

        var summary = new ImplementationSummary
        {
            WorkItemId = context.WorkItem.Id,
            BranchName = agentResult.BranchName,
            PrUrl = prUrl,
            ReviewNotes = reviewNotes
        };
        await summaryWriter.WriteAsync(summary);

        await completionSignalEmitter.EmitAsync(agentResult, prUrl, reviewNotes);
    }
}

using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;

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
            .Run(args);
}

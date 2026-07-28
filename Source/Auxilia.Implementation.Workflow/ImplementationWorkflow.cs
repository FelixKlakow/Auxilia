using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Implementation.Workflow;

/// <summary>
/// Workflow type "implementation": a user story from TFS/Azure DevOps (or mail) is planned,
/// implemented, reviewed (AI + operator), pushed, and closed — one driven author console
/// spanning the whole run, with the operator gating every consequential step from the steering client.
/// See docs/implementation-workflow-design.md.
/// </summary>
public static class ImplementationWorkflow
{
    public const string WorkflowType = "implementation";

    public static Task Main(string[] args) =>
        WorkflowBuilder.Create(WorkflowType)
            .WithLifetime(WorkflowLifetime.LongLiving)
            .Requires<IWorkItemAccess>("work-items",
                new TaskSourceCapabilities { SupportedItemTypes = [ItemType.UserStory] },
                "The story source — TFS/Azure DevOps (or mail); states are read from and "
                + "written back to this source")
            .Requires<ICodingAgent>("coding-agent",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] },
                "The AUTHOR: one interactive CLI instance (visible in the run terminal) spans "
                + "completeness check, plan, and implementation",
                providerTypes: ["claude-code-cli", "github-copilot-cli"])
            .Requires<ICodingAgent>("review-agent",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] },
                "The REVIEWER: a second agent instance for plan and code review; unbound = "
                + "AI review steps are skipped",
                optional: true, providerTypes: ["claude-code-cli", "github-copilot-cli"])
            .Requires<ISourceControlAccess>("repository",
                new SourceControlCapabilities { RequiredPermissions = [Permission.Read, Permission.Write] },
                "The repository the story is implemented in (bind with pushing allowed)")
            .Requires<Auxilia.Workflows.Environment.IExecutionEnvironment>("environment",
                new Auxilia.Workflows.Environment.ExecutionEnvironmentCapabilities(),
                "Software the container comes preinstalled with (SDKs, runtimes, tools)",
                optional: true, allowMultiple: true)
            .DeclaresView<AgentChatEntry>(AgentSessionApplication.ChatViewName,
                ViewRendering.Custom, ViewLifecycle.LiveAndPersisted, AgentChatEntry.RendererKey)
            .DeclaresView<SessionProgressEntry>(AgentSessionApplication.ProgressViewName,
                ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresView<AgentPlanUpdate>(AgentPlanUpdate.ViewName,
                ViewRendering.Custom, ViewLifecycle.LiveAndPersisted)
            .DeclaresView<SteeringWireItem>(Auxilia.Workflows.Steering.OperatorChannel.ViewName,
                ViewRendering.Custom, ViewLifecycle.LiveAndPersisted)
            .DeclaresOutput("implementation-plan", "plan.md", "The approved implementation plan")
            .DeclaresOutput("review-bundle", "review-bundle.md",
                "Changed files, plan, AI verdicts, and the full diff — the reviewable artifact")
            .DeclaresOutput("session-report", SessionReportWriter.FileName,
                "Final run result: outcome, duration, error")
            .DeclaresTrigger(TriggerDeclaration.Manual,
                "Run from the dashboard with a work-item id.")
            .RequiresInput(new WorkflowInputDescriptor(
                "work-item-id", "Work item", Required: true,
                Description: "The user story to implement — its id at the bound source."))
            .RequiresInput(Toggle("completeness-check", "Completeness check",
                "Assess the story first; open questions become an operator form.", on: true))
            .RequiresInput(Toggle("ai-review", "AI review loops",
                "A second agent reviews plan and code, with bounded refinement rounds.", on: true))
            .RequiresInput(new WorkflowInputDescriptor(
                "max-ai-review-rounds", "Max AI review rounds", Required: false,
                Description: "Upper bound per AI review loop.")
            {
                Kind = "Number", DefaultValue = "2"
            })
            .RequiresInput(Toggle("user-plan-gate", "Plan approval gate",
                "You approve or revise the plan before implementation starts.", on: true))
            .RequiresInput(Toggle("user-code-gate", "Code approval gate",
                "You approve or revise the implementation before push.", on: true))
            .RequiresInput(Toggle("artifact-review", "Artifact review gate",
                "An extra gate over the review bundle (diff, plan, verdicts).", on: false))
            .RequiresInput(new WorkflowInputDescriptor(
                "push-mode", "Git push", Required: false,
                Description: "How the workflow pushes after approval — it commits and pushes "
                             + "ITSELF, the agent never touches git.")
            {
                Kind = "Choice", DefaultValue = PushModes.Prompt,
                Choices = [PushModes.Prompt, PushModes.Auto, PushModes.Skip],
                ChoiceLabels = new Dictionary<string, string>
                {
                    [PushModes.Prompt] = "Ask me before pushing",
                    [PushModes.Auto] = "Push automatically after approval",
                    [PushModes.Skip] = "Never push",
                }
            })
            .RequiresInput(new WorkflowInputDescriptor(
                "reviewer-mode", "Reviewer mode", Required: false,
                Description: "Headless: structured second instance. Console: a one-shot CLI in "
                             + "a second tmux session beside the author.")
            {
                Kind = "Choice", DefaultValue = ReviewerModes.Headless,
                Choices = [ReviewerModes.Headless, ReviewerModes.Console],
            })
            .RequiresInput(new WorkflowInputDescriptor(
                "gate-idle-compaction", "Gate idle compaction (minutes)", Required: false,
                Description: "When you idle at a gate longer than this, the author console is "
                             + "compacted so resuming later never re-reads the full context. 0 = off.")
            {
                Kind = "Number", DefaultValue = "10"
            })
            .RequiresInput(new WorkflowInputDescriptor(
                "target-state", "Preferred story state", Required: false,
                Description: "Pre-selected at the closing gate; the choices themselves come "
                             + "from the story source's OWN state vocabulary."))
            // The author console is intrinsic to this workflow — every run has the terminal.
            .WithInteractiveTerminal(AgentConsoleApplication.TerminalPort)
            .RequiresNetworkEndpoint("api.anthropic.com", "Claude API inference (claude-code author/reviewer)")
            .RequiresNetworkEndpoint("claude.ai", "Claude Code CLI account/session endpoints")
            .RequiresNetworkEndpoint("api.githubcopilot.com", "Copilot inference (github-copilot author/reviewer)")
            .RequiresNetworkEndpoint("api.github.com", "GitHub API authentication of the Copilot CLI")
            .RequiresNetworkEndpoint("github.com", "GitHub token validation of the Copilot CLI")
            .RequiresNetworkEndpoint("dev.azure.com", "Azure DevOps work-item API (cloud; on-prem TFS "
                + "hosts ride the run configuration's endpoint extras)")
            .WithApplication(ExecuteAsync)
            .Run(args);

    /// <summary>Schema marker of the steering view — its items are protocol JSON, not a fixed shape.</summary>
    private sealed record SteeringWireItem;

    private static WorkflowInputDescriptor Toggle(string name, string label, string description, bool on)
        => new(name, label, Required: false, Description: description)
        {
            Kind = "Boolean",
            DefaultValue = on ? "true" : "false"
        };

    public static Task RunAsync() => Main(["--test-harness"]);

    private static Task ExecuteAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var context = ImplementationContext.FromEnvironment();
        if (provider.GetService<ISourceControlAccess>()?.WorkingPath is { Length: > 0 } workingPath)
            context = context with { WorkspaceDirectory = workingPath };

        var views = provider.GetService<IViewPublisher>();
        var time = TimeProvider.System;
        var eventViews = views is null ? null : new ConsoleEventViews(views, time);
        var author = new DrivenConsoleSession(
            new TmuxSessionHost(),
            provider.GetService<IConsoleSessionPreparer>(),
            provider.GetService<IConsoleSessionEventSource>(),
            eventViews is null ? null : eventViews.PublishAsync);

        return new ImplementationPipeline(
                provider.GetRequiredService<IWorkItemAccess>(),
                views,
                provider.GetService<IWorkflowInputs>(),
                context,
                author,
                provider.GetService<CodingAgentCredentials>(),
                BuildReviewer(provider, context),
                new ProcessGitRunner(),
                time)
            .RunAsync(cancellationToken);
    }

    private static IReviewRunner? BuildReviewer(IServiceProvider provider, ImplementationContext context)
    {
        if (!context.AiReview)
            return null;
        var credentials = provider.GetKeyedService<CodingAgentCredentials>("review-agent");
        var agent = provider.GetKeyedService<ICodingAgent>("review-agent");
        if (agent is null && credentials is null)
            return null; // review-agent unbound — AI review steps skip
        if (context.ReviewerMode == ReviewerModes.Console && credentials is not null)
            return new ConsoleReviewRunner(
                () => new TmuxSessionHost(), credentials, context.WorkspaceDirectory);
        return agent is null
            ? null
            : new HeadlessReviewRunner(
                agent, context.WorkspaceDirectory,
                provider.GetService<IViewPublisher>(), TimeProvider.System);
    }
}

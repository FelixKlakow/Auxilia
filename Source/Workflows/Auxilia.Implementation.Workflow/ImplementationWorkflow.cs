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
            // What the Dockerfile installs — agent providers match on it, not on provider names.
            .ProvidesTools("claude", "copilot")
            .Requires<ICodingAgent>("coding-agent",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] },
                "The AUTHOR: one interactive CLI instance (visible in the run terminal) spans "
                + "refinement, plan, and implementation by default")
            .Requires<ICodingAgent>("review-agent",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] },
                "The REVIEWER: a second agent instance for plan and code review; unbound = "
                + "AI review steps are skipped",
                optional: true)
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
            // The stage view IS a view: its declared data carries the pipeline's shape (rendered
            // at config time); the runtime items are per-step states, joined by id.
            .DeclaresView<WorkflowStepFlow>(WorkflowStepFlow.ViewName,
                ViewRendering.Custom, ViewLifecycle.LiveAndPersisted,
                WorkflowStepFlow.RendererKey, ImplementationFlow.Steps)
            .DeclaresOutput("implementation-plan", "plan.md", "The approved implementation plan")
            .DeclaresOutput("review-bundle", "review-bundle.md",
                "Changed files, plan, AI verdicts, and the full diff — the reviewable artifact")
            .DeclaresOutput("session-report", SessionReportWriter.FileName,
                "Final run result: outcome, duration, error")
            .DeclaresTrigger(TriggerDeclaration.Manual,
                "Run from the dashboard with a work-item id.")
            .RequiresInput(new WorkflowInputDescriptor(
                "work-item-id", "Work item", Required: true,
                Description: "The user story to implement — its id at the bound source.")
            {
                // A story is different every run by nature — never baked into a configuration.
                PerRun = true
            })
            .RequiresInput(Toggle("refinement-check", "Refinement",
                "Refine the story first; open questions become an operator form.", on: true))
            .RequiresInput(Multiline("refinement-instructions", "Refinement instructions",
                "What the refinement drive tells the agent to do.", ImplementationPrompts.Refinement))
            .RequiresInput(Multiline("plan-instructions", "Plan instructions",
                "What the plan drive tells the agent to do — includes the mermaid design diagram.",
                ImplementationPrompts.Plan))
            .RequiresInput(Multiline("implement-instructions", "Implementation instructions",
                "What the implementation drive tells the agent to do.", ImplementationPrompts.Implement))
            .RequiresInput(Multiline("review-instructions", "Review instructions",
                "The review stance for BOTH AI review passes (plan and code).",
                ImplementationPrompts.Review))
            .RequiresInput(new WorkflowInputDescriptor(
                "plan-agent", "Planning agent", Required: false,
                Description: "Which console plans: the refinement console (shared context, "
                             + "default), a fresh author instance, or the reviewer binding.")
            {
                Kind = "Choice", DefaultValue = StageAgents.Shared,
                Choices = [StageAgents.Shared, StageAgents.Fresh, StageAgents.Reviewer],
                ChoiceLabels = new Dictionary<string, string>
                {
                    [StageAgents.Shared] = "Same console as refinement (keep context)",
                    [StageAgents.Fresh] = "A fresh author instance",
                    [StageAgents.Reviewer] = "The reviewer binding",
                }
            })
            .RequiresInput(new WorkflowInputDescriptor(
                "implement-agent", "Implementation agent", Required: false,
                Description: "Which console implements: the planning console (shared context, "
                             + "default), a fresh author instance, or the reviewer binding.")
            {
                Kind = "Choice", DefaultValue = StageAgents.Shared,
                Choices = [StageAgents.Shared, StageAgents.Fresh, StageAgents.Reviewer],
                ChoiceLabels = new Dictionary<string, string>
                {
                    [StageAgents.Shared] = "Same console as planning (keep context)",
                    [StageAgents.Fresh] = "A fresh author instance",
                    [StageAgents.Reviewer] = "The reviewer binding",
                }
            })
            .RequiresInput(new WorkflowInputDescriptor(
                "review-by", "Reviews are done by", Required: false,
                Description: "The review-agent binding (independent perspective), or the console "
                             + "that refined the story (full context; compaction applies on demand).")
            {
                Kind = "Choice", DefaultValue = ReviewAgents.Reviewer,
                Choices = [ReviewAgents.Reviewer, ReviewAgents.RefinementAgent],
                ChoiceLabels = new Dictionary<string, string>
                {
                    [ReviewAgents.Reviewer] = "The reviewer binding (independent)",
                    [ReviewAgents.RefinementAgent] = "The agent that refined the story",
                }
            })
            .RequiresInput(Toggle("ai-plan-review", "AI plan review",
                "A second agent reviews the PLAN, with bounded refinement rounds.", on: true))
            .RequiresInput(Toggle("ai-code-review", "AI code review",
                "A second agent reviews the IMPLEMENTATION, with bounded refinement rounds.", on: true))
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
                "author-base-prompt", "Author base instructions", Required: false,
                Description: "Instructions the AUTHOR agent always receives - conventions, "
                             + "tech context, tone. Delivered provider-natively "
                             + "(Claude: appended to the system prompt).")
            {
                Kind = "Multiline"
            })
            .RequiresInput(new WorkflowInputDescriptor(
                "reviewer-base-prompt", "Reviewer base instructions", Required: false,
                Description: "Instructions the REVIEWER agent always receives - review focus, "
                             + "severity bar, house rules.")
            {
                Kind = "Multiline"
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

    /// <summary>A multiline input whose DEFAULT is the built-in text — visible and editable.</summary>
    private static WorkflowInputDescriptor Multiline(
        string name, string label, string description, string defaultValue)
        => new(name, label, Required: false, Description: description)
        {
            Kind = "Multiline",
            DefaultValue = defaultValue
        };

    public static Task RunAsync() => Main(["--test-harness"]);

    private static Task ExecuteAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var context = ImplementationContext.FromEnvironment();
        // The per-mount variables are authoritative (a single mount's root already includes its
        // declared working directory). The repository slot's WorkingPath remains the seam for
        // mount-less providers (e.g. the coding-session workspace).
        if (WorkflowEnvironmentVariables.SingleMountRoot() is null
            && provider.GetService<ISourceControlAccess>()?.WorkingPath is { Length: > 0 } workingPath)
            context = context with { WorkspaceDirectory = workingPath };

        var views = provider.GetService<IViewPublisher>();
        var time = TimeProvider.System;
        var eventViews = views is null ? null : new ConsoleEventViews(views, time);

        // Every console CLI in the container posts to the same provider listener — one hub
        // fans it out: view routing once, turn completion per console.
        var eventSource = provider.GetService<IConsoleSessionEventSource>();
        var hub = eventSource is null ? null : new ConsoleSessionEventHub(eventSource);
        if (hub is not null && eventViews is not null)
            hub.OnEvent(eventViews.PublishAsync);

        var consoles = new AgentConsolePool(
            context,
            provider.GetService<CodingAgentCredentials>(),
            provider.GetKeyedService<CodingAgentCredentials>("review-agent"),
            () => new TmuxSessionHost(),
            () => hub?.CreateSource(),
            provider.GetService<IConsoleSessionPreparer>());

        return RunAsync(provider, context, views, consoles, hub, time, cancellationToken);
    }

    private static async Task RunAsync(
        IServiceProvider provider, ImplementationContext context, IViewPublisher? views,
        AgentConsolePool consoles, ConsoleSessionEventHub? hub, TimeProvider time,
        CancellationToken cancellationToken)
    {
        // The story source rides into the SESSION as MCP: the agent reads work-item detail
        // (relations, parents, states) itself instead of only the materialized story file.
        Auxilia.Workflows.TaskSource.Mcp.WorkItemAccessMcpTools? workItemTools = null;
        if (provider.GetService<IConsoleSessionPreparer>() is { } preparer)
        {
            workItemTools = new Auxilia.Workflows.TaskSource.Mcp.WorkItemAccessMcpTools(
                "work-items", provider.GetRequiredService<IWorkItemAccess>());
            await workItemTools.StartAsync(
                new Auxilia.Workflows.Mcp.HttpMcpTransportConfig(
                    "http://127.0.0.1:0", "auxilia-work-items"),
                cancellationToken);
            if (workItemTools.CurrentTransport
                is Auxilia.Workflows.Mcp.HttpMcpTransportConfig { EndpointUrl: { Length: > 0 } url })
                await preparer.RegisterMcpServerAsync(
                    "auxilia-work-items", url, context.WorkspaceDirectory, cancellationToken);
        }

        try
        {
            await new ImplementationPipeline(
                    provider.GetRequiredService<IWorkItemAccess>(),
                    views,
                    provider.GetService<IWorkflowInputs>(),
                    context,
                    consoles,
                    BuildReviewer(provider, context),
                    new ProcessGitRunner(),
                    time)
                .RunAsync(cancellationToken);
        }
        finally
        {
            if (workItemTools is not null)
                await workItemTools.StopAsync(CancellationToken.None);
            if (hub is not null)
                await hub.DisposeAsync();
        }
    }

    private static IReviewRunner? BuildReviewer(IServiceProvider provider, ImplementationContext context)
    {
        if (!context.AiPlanReview && !context.AiCodeReview)
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

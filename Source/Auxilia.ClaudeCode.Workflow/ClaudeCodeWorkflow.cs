using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ClaudeCode.Workflow;

/// <summary>
/// Runs an autonomous Claude Code session against the run's workspace, streaming the agent's
/// conversation live to the dashboard's BlazorAgentView renderer.
/// </summary>
public static class ClaudeCodeWorkflow
{
    public const string WorkflowType = "claude-code";

    public static Task Main(string[] args) =>
        WorkflowBuilder.Create(WorkflowType)
            .Requires<ICodingAgent>("coding-agent",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] },
                "The autonomous coding agent executing the instruction (e.g. the Claude Code CLI)",
                // This package's image bundles the Claude CLI only — narrow the ICodingAgent match.
                providerTypes: ["claude-code-cli"])
            .Requires<ISourceControlAccess>("repository",
                new SourceControlCapabilities { RequiredPermissions = [Permission.Read] },
                "The repositories the agent works on — prepared into the run's workspace before launch",
                optional: true, allowMultiple: true)
            .Requires<Auxilia.Workflows.Environment.IExecutionEnvironment>("environment",
                new Auxilia.Workflows.Environment.ExecutionEnvironmentCapabilities(),
                "Software the session's container comes preinstalled with (SDKs, runtimes, tools) — "
                + "pick any combination; the runner composes and caches the matching image",
                optional: true, allowMultiple: true)
            .DeclaresView<AgentChatEntry>(AgentSessionApplication.ChatViewName,
                ViewRendering.Custom, ViewLifecycle.LiveAndPersisted, AgentChatEntry.RendererKey)
            .DeclaresView<SessionProgressEntry>(AgentSessionApplication.ProgressViewName,
                ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            // The agent's current plan (full snapshots) — rendered as a live checklist.
            .DeclaresView<AgentPlanUpdate>(AgentPlanUpdate.ViewName,
                ViewRendering.Custom, ViewLifecycle.LiveAndPersisted)
            // The operator loop: questions, guidance, halt — the steering client reads/answers this view.
            .DeclaresView<SteeringWireItem>(Auxilia.Workflows.Steering.OperatorChannel.ViewName,
                ViewRendering.Custom, ViewLifecycle.LiveAndPersisted)
            .DeclaresOutput("session-report", SessionReportWriter.FileName,
                "Final agent session result: summary, turn count, duration, and cost")
            .DeclaresTrigger(TriggerDeclaration.Manual,
                "Run from the dashboard with an instruction — the agent's task.")
            .RequiresInput(new WorkflowInputDescriptor(
                "instruction", "Instruction", Required: false,
                Description: "The task the agent executes autonomously — a mail's subject/body for "
                             + "triggered runs. Required unless the session is multi-turn (there "
                             + "the first instruction may arrive live from the steering client).")
            {
                Kind = "Multiline"
            })
            .RequiresInput(new WorkflowInputDescriptor(
                AgentViewModes.InputName, "View", Required: false,
                Description: "How you experience the session. Advanced: the parsed conversation "
                             + "view with steering, permissions, and the plan. Console: the CLI "
                             + "runs interactively behind the platform's web terminal — the real "
                             + "Claude Code console, remote-controlled; steering inputs do not "
                             + "apply there.")
            {
                Kind = "Choice",
                DefaultValue = AgentViewModes.Advanced,
                Choices = [AgentViewModes.Advanced, AgentViewModes.Console],
                ChoiceLabels = new Dictionary<string, string>
                {
                    [AgentViewModes.Advanced] = "Advanced (conversation view)",
                    [AgentViewModes.Console] = "Console (live terminal)",
                }
            })
            .RequiresInput(new WorkflowInputDescriptor(
                "multi-turn", "Multi-turn session", Required: false,
                Description: "Keep the session alive after each turn: the agent announces the "
                             + "turn's end and waits for your next instruction from the steering client "
                             + "until you end the session. Off: the run finishes when the agent "
                             + "completes the instruction.")
            {
                Kind = "Boolean",
                DefaultValue = "false"
            })
            .RequiresInput(new WorkflowInputDescriptor(
                "permission-mode", "Permissions", Required: false,
                Description: "How the agent's tool-permission requests are handled. Ask me: every "
                             + "request becomes a decision card you answer from the steering client. "
                             + "Auto-approve: requests are allowed automatically — the isolated "
                             + "container stays the safety net, and you can still guide or halt. "
                             + "Changeable live while the session runs.")
            {
                Kind = "Choice",
                DefaultValue = AgentPermissionModes.AskOperator,
                Choices = [AgentPermissionModes.AskOperator, AgentPermissionModes.AutoAllow],
                ChoiceLabels = new Dictionary<string, string>
                {
                    [AgentPermissionModes.AskOperator] = "Ask me for every tool use",
                    [AgentPermissionModes.AutoAllow] = "Auto-approve (sandboxed)",
                }
            })
            .RequiresInput(new WorkflowInputDescriptor(
                "push-policy", "Git pushes", Required: false,
                Description: "Pushes have their OWN policy, independent of the general permission "
                             + "mode — auto-approve edits but confirm each push, or the inverse. "
                             + "Only matters for repositories bound with pushing allowed. "
                             + "Changeable live while the session runs.")
            {
                Kind = "Choice",
                DefaultValue = AgentPermissionModes.AskOperator,
                Choices = [AgentPermissionModes.AskOperator, AgentPermissionModes.AutoAllow],
                ChoiceLabels = new Dictionary<string, string>
                {
                    [AgentPermissionModes.AskOperator] = "Ask me before each push",
                    [AgentPermissionModes.AutoAllow] = "Auto-approve pushes",
                }
            })
            .DeclaresTrigger(TriggerDeclaration.Mailbox,
                "A filtered mail starts a run; subject and body become the instruction.")
            .DeclaresTrigger(TriggerDeclaration.Artifact,
                "Another workflow's output artifact starts a run.")
            // Console mode: the CLI runs interactively in tmux+ttyd behind the platform's
            // authenticated web terminal — exposed ONLY for runs whose view-mode is console.
            .WithInteractiveTerminal(
                AgentConsoleApplication.TerminalPort,
                new InteractiveTerminalGate(AgentViewModes.InputName, AgentViewModes.Console))
            // Baseline egress policy (ARCHITECTURE §10). The CLI runs with nonessential
            // traffic disabled, so inference is the only endpoint it needs — plus the
            // account/session endpoints an interactive console signs in through.
            .RequiresNetworkEndpoint("api.anthropic.com", "Claude API inference calls of the Claude Code CLI")
            .RequiresNetworkEndpoint("claude.ai", "Claude Code CLI account/session endpoints (console mode)")
            .WithApplication(ExecuteAsync)
            .Run(args);

    /// <summary>Schema marker of the steering view — its items are protocol JSON, not a fixed shape.</summary>
    private sealed record SteeringWireItem;

    public static Task RunAsync() => Main(["--test-harness"]);

    private static Task ExecuteAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var context = AgentSessionContext.FromEnvironment();
        // A bound repository slot with a local working copy (the Workspace Manager's per-run
        // clone) becomes the agent's working directory; without one the run keeps its default.
        if (provider.GetService<ISourceControlAccess>()?.WorkingPath is { Length: > 0 } workingPath)
            context = context with { WorkspaceDirectory = workingPath };

        // Console mode drives the REAL interactive CLI in tmux+ttyd (the slot's raw
        // credentials, the web terminal as the surface); advanced mode is the parsed
        // stream-json conversation. One workflow, the view-mode input decides.
        if (context.IsConsole)
            return new AgentConsoleApplication(
                    new TmuxSessionHost(),
                    provider.GetService<IViewPublisher>(),
                    context,
                    provider.GetService<CodingAgentCredentials>(),
                    TimeProvider.System,
                    provider.GetService<IConsoleSessionPreparer>())
                .RunAsync(cancellationToken);

        return new AgentSessionApplication(
                provider.GetRequiredService<ICodingAgent>(),
                provider.GetService<IViewPublisher>(),
                context,
                TimeProvider.System,
                provider.GetService<IWorkflowInputs>())
            .RunAsync(cancellationToken);
    }
}

using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodingSession.Workflow;

/// <summary>
/// Live coding session: a real coding-agent CLI — Claude Code, GitHub Copilot, or Codex,
/// whichever provider the coding-agent slot binds (the operator's own credential injected
/// just-in-time from a personal slot instance) — runs interactively in this container
/// against the mounted repository, reachable through the platform's authenticated web
/// terminal. When the CLI exits, the run completes and publishes which files changed.
/// </summary>
public static class CodingSessionWorkflow
{
    public const string WorkflowType = "coding-session";

    public static Task Main(string[] args) =>
        WorkflowBuilder.Create(WorkflowType)
            .WithLifetime(WorkflowLifetime.LongLiving)
            .WithInteractiveTerminal(SessionRunContext.DefaultTerminalPort)
            .RequiresSourceControl("repository",
                new SourceControlCapabilities { RequiredPermissions = [Permission.Read, Permission.Write] },
                "The repository the live session works on (mounted by the Workspace Manager)")
            // Fixed purpose, always bound: the session IS a CLI-agent session, so the account
            // it runs with is a required slot. ANY coding-agent provider works — Claude Code,
            // GitHub Copilot, or Codex — the session simply launches that provider's CLI.
            .Requires<ICodingAgent>("coding-agent",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] },
                "The agent account the live session signs in with (Claude Code, GitHub Copilot, or Codex)")
            .Requires<Auxilia.Workflows.TaskSource.IWorkItemAccess>("work-items",
                new Auxilia.Workflows.TaskSource.TaskSourceCapabilities
                {
                    SupportedItemTypes = [Auxilia.Workflows.TaskSource.ItemType.UserStory]
                },
                "Optional: the triggering mail — its attachments land in the workspace",
                optional: true)
            .DeclaresView<SessionProgressEntry>(CodingSessionApplication.ProgressViewName,
                ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresOutput("coding-session-result", CodingSessionResult.FileName,
                "Branch and changed-file names of the finished live session")
            .DeclaresTrigger(TriggerDeclaration.Manual,
                "Start a session from the dashboard, then open the live terminal.")
            .RequiresInput("instruction", "Instruction", required: false,
                "Optional context for the session — the operator drives the CLI live either way.")
            .DeclaresTrigger(TriggerDeclaration.Mailbox,
                "A filtered mail starts the session; chain a notifier onto the result artifact for the reply.")
            // Each CLI needs its inference plus account/session endpoints — nothing else.
            // The union of all three agents is declared; the effective policy stays clamped
            // by the platform ceiling, and unused endpoints simply see no traffic.
            .RequiresNetworkEndpoint("api.anthropic.com", "Claude API inference calls of the Claude Code CLI")
            .RequiresNetworkEndpoint("claude.ai", "Claude Code CLI account/session endpoints")
            .RequiresNetworkEndpoint("api.githubcopilot.com", "Copilot CLI inference calls")
            .RequiresNetworkEndpoint("api.github.com", "Copilot CLI account/session endpoints")
            .RequiresNetworkEndpoint("api.openai.com", "Codex CLI inference calls")
            .RequiresNetworkEndpoint("chatgpt.com", "Codex CLI account/session endpoints")
            .WithApplication(ExecuteAsync)
            .Run(args);

    public static Task RunAsync() => Main(["--test-harness"]);

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var sourceControl = provider.GetRequiredService<ISourceControlAccess>();
        // JIT-activated account credentials: exported to the CLI only via the session's
        // process environment. The CODING_SESSION_CLI env override (system tests' stub)
        // wins over the slot's CLI path.
        var credentials = provider.GetService<CodingAgentCredentials>();
        var context = SessionRunContext.FromEnvironment() with
        {
            WorkspaceDirectory = sourceControl.WorkingPath
        };
        if (credentials is not null)
        {
            context = context with
            {
                SessionCommand = string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("CODING_SESSION_CLI"))
                    ? credentials.CliPath
                    : context.SessionCommand,
                SessionEnvironment = credentials.ToEnvironment()
            };
        }
        await new CodingSessionApplication(
                new TmuxSessionHost(),
                new ProcessGitRunner(),
                provider.GetService<IViewPublisher>(),
                context,
                TimeProvider.System,
                provider.GetService<Auxilia.Workflows.TaskSource.IWorkItemAccess>())
            .RunAsync(cancellationToken);
    }
}

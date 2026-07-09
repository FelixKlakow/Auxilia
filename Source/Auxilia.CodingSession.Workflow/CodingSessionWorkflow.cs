using Auxilia.ClaudeCode.Workflow;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodingSession.Workflow;

/// <summary>
/// Live coding session: the real Claude Code CLI (the operator's own account, OAuth token
/// injected just-in-time from a personal slot instance) runs interactively in this container
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
            // Fixed purpose, always bound: the session IS a Claude Code session, so the
            // account it runs with is a required slot (bind a connected Claude account).
            .Requires<ICodingAgent>("coding-agent",
                new AiCapabilities { MinContextWindow = 128_000, SupportedModalities = [Modality.Text] },
                "The Claude account the live session signs in with (connected account preferred, API key fallback)")
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
            // The CLI needs inference plus its account/session endpoints — nothing else.
            .RequiresNetworkEndpoint("api.anthropic.com", "Claude API inference calls of the Claude Code CLI")
            .RequiresNetworkEndpoint("claude.ai", "Claude Code CLI account/session endpoints")
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

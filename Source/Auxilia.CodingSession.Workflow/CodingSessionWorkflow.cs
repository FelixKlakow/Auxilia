using Auxilia.Workflows;
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
            .RequiresSourceControl("repository",
                new SourceControlCapabilities { RequiredPermissions = [Permission.Read, Permission.Write] },
                "The repository the live session works on (mounted by the Workspace Manager)")
            .DeclaresView<SessionProgressEntry>(CodingSessionApplication.ProgressViewName,
                ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresOutput("coding-session-result", CodingSessionResult.FileName,
                "Branch and changed-file names of the finished live session")
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
        var context = SessionRunContext.FromEnvironment() with
        {
            WorkspaceDirectory = sourceControl.WorkingPath
        };
        await new CodingSessionApplication(
                new TmuxSessionHost(),
                new ProcessGitRunner(),
                provider.GetService<IViewPublisher>(),
                context,
                TimeProvider.System)
            .RunAsync(cancellationToken);
    }
}

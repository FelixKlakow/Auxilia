using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
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
                "The autonomous coding agent executing the instruction (e.g. the Claude Code CLI)")
            .DeclaresView<AgentChatEntry>(ClaudeCodeApplication.ChatViewName,
                ViewRendering.Custom, ViewLifecycle.LiveAndPersisted, AgentChatEntry.RendererKey)
            .DeclaresView<SessionProgressEntry>(ClaudeCodeApplication.ProgressViewName,
                ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresOutput("session-report", SessionReportWriter.FileName,
                "Final agent session result: summary, turn count, duration, and cost")
            // Baseline egress policy (ARCHITECTURE §10). The CLI runs with nonessential
            // traffic disabled, so inference is the only endpoint it needs.
            .RequiresNetworkEndpoint("api.anthropic.com", "Claude API inference calls of the Claude Code CLI")
            .WithApplication(ExecuteAsync)
            .Run(args);

    public static Task RunAsync() => Main(["--test-harness"]);

    private static Task ExecuteAsync(IServiceProvider provider, CancellationToken cancellationToken)
        => new ClaudeCodeApplication(
                provider.GetRequiredService<ICodingAgent>(),
                provider.GetService<IViewPublisher>(),
                ClaudeCodeRunContext.FromEnvironment(),
                TimeProvider.System)
            .RunAsync(cancellationToken);
}

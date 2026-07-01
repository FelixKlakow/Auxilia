using Auxilia.Workflows.Views;

namespace Auxilia.ClaudeCode.Workflow;

/// <summary>
/// Orchestrates one agent run: surfaces the instruction and every agent turn on the
/// "agent-conversation" view, tracks lifecycle on "progress", and writes the session report —
/// also for failed runs, so the report is the durable record either way.
/// </summary>
public sealed class ClaudeCodeApplication(
    ICodingAgent agent,
    IViewPublisher? views,
    ClaudeCodeRunContext context,
    TimeProvider time)
{
    public const string ChatViewName = "agent-conversation";
    public const string ProgressViewName = "progress";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await PublishProgressAsync("session", "started", cancellationToken);
        await PublishChatAsync(
            new AgentChatEntry(AgentChatRole.User, context.Instruction, time.GetUtcNow()),
            cancellationToken);

        var result = await agent.RunAsync(
            new CodingAgentRequest(context.Instruction, context.WorkspaceDirectory),
            PublishChatAsync,
            cancellationToken);

        await PublishProgressAsync("session", result.Success ? "completed" : "failed", cancellationToken);

        var report = new SessionReport(
            context.Instruction, result.Success, result.Summary, result.TurnCount,
            result.TotalCostUsd, result.DurationMs, result.ErrorMessage, time.GetUtcNow());
        await new SessionReportWriter(context.OutputDirectory).WriteAsync(report, cancellationToken);
        await PublishProgressAsync("report", "written", cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(
                result.ErrorMessage ?? "The coding agent reported failure.");
    }

    private Task PublishChatAsync(AgentChatEntry entry, CancellationToken cancellationToken)
        => views?.PublishAsync(ChatViewName, entry, cancellationToken) ?? Task.CompletedTask;

    private Task PublishProgressAsync(string phase, string message, CancellationToken cancellationToken)
        => views?.PublishAsync(
               ProgressViewName, new SessionProgressEntry(phase, message, time.GetUtcNow()), cancellationToken)
           ?? Task.CompletedTask;
}

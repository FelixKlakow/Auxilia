using Auxilia.Workflows;
using Auxilia.Workflows.Steering;
using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// Orchestrates one agent run: surfaces the instruction and every agent turn on the
/// "agent-conversation" view, tracks lifecycle on "progress", and writes the session report —
/// also for failed runs, so the report is the durable record either way.
/// <para>
/// With an input channel present the run is OPERATOR-STEERABLE through the SDK's
/// <see cref="OperatorChannel"/>: the agent's questions surface as steering client forms (and block until
/// answered), operator guidance reaches the agent mid-session, and a halt cancels the session.
/// Without one the run is fully autonomous — same workflow, no steering surface announced.
/// </para>
/// </summary>
public sealed class AgentSessionApplication(
    ICodingAgent agent,
    IViewPublisher? views,
    AgentSessionContext context,
    TimeProvider time,
    IWorkflowInputs? inputs = null)
{
    public const string ChatViewName = "agent-conversation";
    public const string ProgressViewName = "progress";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await PublishProgressAsync("session", "started", cancellationToken);
        await PublishChatAsync(
            new AgentChatEntry(AgentChatRole.User, context.Instruction, time.GetUtcNow()),
            cancellationToken);

        OperatorChannel? channel = null;
        if (views is not null && inputs is not null)
            channel = await OperatorChannel.StartAsync(views, inputs, cancellationToken);

        try
        {
            // A halt from the steering client cancels the agent session — not the whole workflow teardown.
            using var session = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, channel?.HaltToken ?? CancellationToken.None);

            CodingAgentResult result;
            try
            {
                result = await agent.RunAsync(
                    new CodingAgentRequest(
                        context.Instruction, context.WorkspaceDirectory,
                        channel is null ? null : new SteeredInteraction(channel, PublishChatAsync, time)),
                    PublishChatAsync,
                    session.Token);
            }
            catch (OperationCanceledException) when (channel?.HaltToken.IsCancellationRequested == true)
            {
                result = new CodingAgentResult(
                    Success: false, Summary: null, ErrorMessage: "The session was halted by the operator.");
            }

            await PublishProgressAsync("session", result.Success ? "completed" : "failed", cancellationToken);

            var report = new SessionReport(
                context.Instruction, result.Success, result.Summary, result.TurnCount,
                result.TotalCostUsd, result.DurationMs, result.ErrorMessage, time.GetUtcNow());
            await new SessionReportWriter(context.OutputDirectory).WriteAsync(report, cancellationToken);
            await PublishProgressAsync("report", "written", cancellationToken);

            if (channel is not null)
                await channel.EndSessionAsync(result.Success, result.ErrorMessage, cancellationToken);

            if (!result.Success)
                throw new InvalidOperationException(
                    result.ErrorMessage ?? "The coding agent reported failure.");
        }
        finally
        {
            if (channel is not null)
                await channel.DisposeAsync();
        }
    }

    private Task PublishChatAsync(AgentChatEntry entry, CancellationToken cancellationToken)
        => views?.PublishAsync(ChatViewName, entry, cancellationToken) ?? Task.CompletedTask;

    private Task PublishProgressAsync(string phase, string message, CancellationToken cancellationToken)
        => views?.PublishAsync(
               ProgressViewName, new SessionProgressEntry(phase, message, time.GetUtcNow()), cancellationToken)
           ?? Task.CompletedTask;

    /// <summary>
    /// Bridges the agent's interaction seam onto the operator channel: one question = a one-question
    /// steering client form; question and answer are also mirrored into the conversation view.
    /// </summary>
    private sealed class SteeredInteraction(
        OperatorChannel channel,
        Func<AgentChatEntry, CancellationToken, Task> chat,
        TimeProvider time) : IAgentInteraction
    {
        public async Task<AgentAnswer> AskAsync(AgentQuestion question, CancellationToken cancellationToken)
        {
            await chat(new AgentChatEntry(
                AgentChatRole.System, question.Prompt, time.GetUtcNow(), Label: "Question to the operator"),
                cancellationToken);

            var answers = await channel.AskAsync(
            [
                new OperatorQuestion(
                    "q1", question.Prompt,
                    question.Options.Select(o => new OperatorOption(o.Id, o.Label, o.Description)).ToList(),
                    question.MultiSelect, question.AllowFreeText)
            ], cancellationToken);

            var answer = answers.FirstOrDefault(a => a.QuestionId == "q1")
                         ?? new OperatorAnswer("q1", [], null);
            var display = string.Join(", ", answer.SelectedIds)
                          + (answer.FreeText is { Length: > 0 } text
                              ? (answer.SelectedIds.Count > 0 ? " — " : "") + text
                              : string.Empty);
            await chat(new AgentChatEntry(
                AgentChatRole.User, display.Length > 0 ? display : "(no selection)",
                time.GetUtcNow(), Label: "Operator's answer"), cancellationToken);

            return new AgentAnswer(answer.SelectedIds, answer.FreeText);
        }

        public async Task<string> WaitForGuidanceAsync(CancellationToken cancellationToken)
        {
            var guidance = await channel.WaitForGuidanceAsync(cancellationToken);
            await chat(new AgentChatEntry(
                AgentChatRole.User, guidance, time.GetUtcNow(), Label: "Operator guidance"),
                cancellationToken);
            return guidance;
        }
    }
}

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
        if (context.Instruction.Length > 0)
            await PublishChatAsync(
                new AgentChatEntry(AgentChatRole.User, context.Instruction, time.GetUtcNow()),
                cancellationToken);

        OperatorChannel? channel = null;
        if (views is not null && inputs is not null)
            channel = await OperatorChannel.StartAsync(views, inputs, cancellationToken,
                extraCapabilities: context.MultiTurn ? ["end"] : null);

        // A multi-turn session lives from operator input — without the steering surface there is
        // no way to ever send a next turn (or to end the session).
        if (context.MultiTurn && channel is null)
            throw new InvalidOperationException(
                "A multi-turn session needs the platform connection (views + inputs) for operator steering.");

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
                        channel is null ? null : new SteeredInteraction(channel, PublishChatAsync, time),
                        context.PermissionMode)
                    {
                        PushPolicy = context.PushPolicy,
                        // The agent's plan revisions land on the "plan" view as full snapshots.
                        OnPlanUpdate = views is null
                            ? null
                            : (items, token) => views.PublishAsync(
                                AgentPlanUpdate.ViewName, new AgentPlanUpdate(items), token),
                        MultiTurn = context.MultiTurn,
                        EndToken = channel?.EndToken ?? CancellationToken.None,
                        // The agent's live model/effort vocabulary feeds the steering client dropdowns.
                        OnSessionVocabulary = channel is null
                            ? null
                            : (models, token) => channel.PublishSessionVocabularyAsync(
                                models.Select(m =>
                                    (m.Id, m.Label, m.ReasoningEfforts, m.DefaultReasoningEffort)).ToList(),
                                token),
                        // Each finished turn is announced on the steering view (the steering client's
                        // notification cue) and mirrored into the conversation.
                        OnTurnEnded = channel is null
                            ? null
                            : async (turn, summary, token) =>
                            {
                                await channel.PublishTurnEndedAsync(turn, summary, token);
                                await PublishChatAsync(new AgentChatEntry(
                                    AgentChatRole.System,
                                    $"Turn finished ({turn} so far) — the session is waiting for your next instruction.",
                                    time.GetUtcNow(), Label: "Session"), token);
                            },
                    },
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
                    question.MultiSelect, question.AllowFreeText, question.Detail)
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

        public async Task<AgentSetting> WaitForSettingAsync(CancellationToken cancellationToken)
        {
            var setting = await channel.WaitForSettingAsync(cancellationToken);
            return new AgentSetting(setting.Key, setting.Value);
        }
    }
}

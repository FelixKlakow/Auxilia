using System.Diagnostics;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Views;

namespace Auxilia.Slots.GitHubCopilot;

/// <summary>
/// Runs the GitHub Copilot CLI through the Copilot SDK's session protocol — full parity with the
/// interactive Claude provider: permission requests become operator decision cards (governed by
/// the permission mode and the per-action push policy), the CLI's own user-input questions become
/// operator forms, guidance arrives as follow-up messages, a live model switch maps to
/// <c>SetModelAsync</c>, and assistant checklists publish plan snapshots. Without an interaction
/// the session auto-approves — the container is the sandbox.
/// <para>
/// Multi-turn: after each completed turn the session announces the boundary
/// (<see cref="CodingAgentRequest.OnTurnEnded"/>) and idles until the operator's next guidance,
/// which becomes the next turn's prompt; the end token finishes the session successfully.
/// </para>
/// </summary>
public sealed class CopilotSdkAgent(
    CopilotCliOptions options,
    TimeProvider? time = null) : ICodingAgent
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<CodingAgentResult> RunAsync(
        CodingAgentRequest request,
        Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = new SessionState(request.PermissionMode, request.PushPolicy);
        using var sessionScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(options.CliPath, null),
            GitHubToken = options.Token,
            WorkingDirectory = request.WorkspaceDirectory,
        });
        await client.StartAsync(sessionScope.Token);

        var turns = 0;
        var plan = new List<AgentPlanItem>();
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            WorkingDirectory = request.WorkspaceDirectory,
            Streaming = false,
            OnPermissionRequest = (permission, _) =>
                DecidePermissionAsync(permission, request, settings, onChatEntry, sessionScope.Token),
            OnUserInputRequest = (question, _) =>
                AnswerUserInputAsync(question, request, onChatEntry, sessionScope.Token),
        }, sessionScope.Token);

        using var events = session.On<SessionEvent>(evt =>
            _ = OnSessionEventAsync(evt, request, plan, onChatEntry, sessionScope.Token));

        // The session's LIVE vocabulary: switchable models + their reasoning efforts — fed to
        // the steering client dropdowns; failures just leave the dropdowns editable, never static.
        if (request.OnSessionVocabulary is { } vocabulary)
        {
            try
            {
                var models = await client.ListModelsAsync(sessionScope.Token);
                var options = (models ?? [])
                    .Select(m => new AgentModelOption(
                        m.Id ?? "", m.Name ?? m.Id ?? "",
                        m.SupportedReasoningEfforts?.ToList() ?? [],
                        m.DefaultReasoningEffort))
                    .Where(m => m.Id.Length > 0)
                    .ToList();
                if (options.Count > 0)
                    await vocabulary(options, sessionScope.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Vocabulary is a nicety — the session runs fine without it.
            }
        }

        var multiTurn = request.MultiTurn && request.Interaction is not null;
        Task? guidanceTask = null;
        Task? settingsTask = null;
        if (request.Interaction is { } interaction)
        {
            // Multi-turn consumes guidance AS the next turn (awaiting each turn's completion),
            // so the fire-and-forget guidance pump runs only for single-turn sessions —
            // guidance sent while a turn runs queues up and becomes the following turn.
            if (!multiTurn)
                guidanceTask = PumpGuidanceAsync(session, interaction, sessionScope.Token);
            settingsTask = PumpSettingsAsync(session, interaction, settings, onChatEntry, sessionScope.Token);
        }

        try
        {
            string? lastSummary = null;
            // The first turn is the dispatched instruction; a multi-turn session may start
            // without one — the operator sends it live.
            var prompt = request.Instruction is { Length: > 0 } ? request.Instruction : null;
            if (prompt is null && !multiTurn)
                return new CodingAgentResult(
                    Success: false, Summary: null,
                    ErrorMessage: "A single-turn Copilot session needs an instruction.");
            do
            {
                prompt ??= await WaitForNextTurnAsync(
                    request.Interaction!, request.EndToken, sessionScope.Token);
                if (prompt is null)
                    break; // the operator ended the session while it was idle

                var reply = await session.SendAndWaitAsync(prompt, null, sessionScope.Token);
                prompt = null;
                turns++;
                if (reply?.Data?.Content is { Length: > 0 } content)
                    lastSummary = content;
                if (multiTurn && !request.EndToken.IsCancellationRequested
                    && request.OnTurnEnded is { } onTurnEnded)
                    await onTurnEnded(turns, lastSummary, sessionScope.Token);
            }
            while (multiTurn && !request.EndToken.IsCancellationRequested);

            stopwatch.Stop();
            return new CodingAgentResult(
                Success: true,
                Summary: lastSummary ?? "The Copilot session completed.",
                TurnCount: turns,
                DurationMs: stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new CodingAgentResult(
                Success: false, Summary: null,
                DurationMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: $"The Copilot session failed: {ex.Message}");
        }
        finally
        {
            await sessionScope.CancelAsync();
            foreach (var pump in new[] { guidanceTask, settingsTask })
                if (pump is not null)
                    try { await pump; }
                    catch (OperationCanceledException) { /* stopped with the session */ }
        }
    }

    /// <summary>Session events → chat entries + plan snapshots (checklists in assistant text).</summary>
    private async Task OnSessionEventAsync(
        SessionEvent evt, CodingAgentRequest request, List<AgentPlanItem> plan,
        Func<AgentChatEntry, CancellationToken, Task> onChatEntry, CancellationToken ct)
    {
        try
        {
            switch (evt)
            {
                case AssistantMessageEvent assistant when assistant.Data?.Content is { Length: > 0 } content:
                    await onChatEntry(new AgentChatEntry(
                        AgentChatRole.Assistant, content, _time.GetUtcNow()), ct);
                    await PublishChecklistAsync(content, request, plan, ct);
                    break;
                case ToolExecutionStartEvent { Data: { } start }:
                    await onChatEntry(new AgentChatEntry(
                        AgentChatRole.Tool, start.Arguments?.ToString() ?? "", _time.GetUtcNow(),
                        ToolName: start.ToolName ?? "tool", ToolState: "Running",
                        ToolUseId: start.ToolCallId), ct);
                    break;
                case ToolExecutionCompleteEvent { Data: { } complete }:
                    await onChatEntry(new AgentChatEntry(
                        AgentChatRole.Tool,
                        complete.Error?.Message ?? "",
                        _time.GetUtcNow(),
                        ToolState: complete.Success == true ? "Success" : "Error",
                        ToolUseId: complete.ToolCallId), ct);
                    break;
                case SessionErrorEvent error when error.Data?.Message is { Length: > 0 } message:
                    await onChatEntry(new AgentChatEntry(
                        AgentChatRole.System, message, _time.GetUtcNow(), Label: "Session error"), ct);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended while an event was in flight.
        }
    }

    private static async Task PublishChecklistAsync(
        string content, CodingAgentRequest request, List<AgentPlanItem> plan, CancellationToken ct)
    {
        if (request.OnPlanUpdate is not { } publish)
            return;
        var items = content.Split('\n')
            .Select(line => CopilotCliAgent.TryParseChecklistLine(line.Trim()))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
        if (items.Count == 0)
            return;
        plan.Clear();
        plan.AddRange(items);
        await publish(plan.ToList(), ct);
    }

    /// <summary>
    /// Permission requests through the SAME policy model as the Claude provider: pushes are
    /// governed by the push policy, everything else by the permission mode; ask-mode raises an
    /// operator decision card with the request rendered as detail.
    /// </summary>
    private async Task<PermissionDecision> DecidePermissionAsync(
        PermissionRequest permission, CodingAgentRequest request, SessionState settings,
        Func<AgentChatEntry, CancellationToken, Task> onChatEntry, CancellationToken ct)
    {
        var (summary, detail, isPush) = Describe(permission);
        var policy = isPush ? settings.PushPolicy : settings.PermissionMode;

        if (request.Interaction is not { } interaction
            || string.Equals(policy, AgentPermissionModes.AutoAllow, StringComparison.OrdinalIgnoreCase))
        {
            await onChatEntry(new AgentChatEntry(
                AgentChatRole.System,
                $"{summary} was allowed automatically ({(isPush ? "push policy" : "permission mode")}: auto-allow).",
                _time.GetUtcNow(), Label: "Permission", DetailTag: "permissions"), ct);
            return PermissionDecision.ApproveOnce();
        }

        var answer = await interaction.AskAsync(new AgentQuestion(
            $"{summary}. Allow it?",
            [
                new AgentQuestionOption("allow", "Allow once"),
                new AgentQuestionOption("deny", "Deny"),
            ],
            MultiSelect: false, AllowFreeText: false, Detail: detail), ct);
        var allowed = answer.SelectedIds.Contains("allow", StringComparer.OrdinalIgnoreCase);
        await onChatEntry(new AgentChatEntry(
            AgentChatRole.System,
            $"{summary} was {(allowed ? "allowed" : "denied")} by the operator.",
            _time.GetUtcNow(), Label: "Permission", DetailTag: "permissions"), ct);
        return allowed ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject();
    }

    /// <summary>Human summary + detail block for a permission request; flags the PUSH action kind.</summary>
    internal static (string Summary, string? Detail, bool IsPush) Describe(PermissionRequest permission)
        => permission switch
        {
            PermissionRequestShell shell => (
                "The agent wants to run a command",
                shell.FullCommandText,
                shell.FullCommandText is { Length: > 0 } text
                    && System.Text.RegularExpressions.Regex.IsMatch(text, @"\bgit\b[^|;&]*\bpush\b")),
            PermissionRequestWrite write => (
                $"The agent wants to write '{write.FileName}'", write.Diff, false),
            PermissionRequestRead read => (
                $"The agent wants to read '{read.Path}'", read.Intention, false),
            PermissionRequestUrl url => (
                "The agent wants to fetch a URL", url.Url, false),
            PermissionRequestMcp mcp => (
                $"The agent wants to use the MCP tool '{mcp.ToolName}'", mcp.Args?.ToString(), false),
            PermissionRequestCustomTool custom => (
                $"The agent wants to use the tool '{custom.ToolName}'", custom.Args?.ToString(), false),
            _ => ($"The agent requests permission ({permission.Kind})", null, false),
        };

    /// <summary>The CLI's own questions become operator forms — a question, never a permission.</summary>
    private async Task<UserInputResponse> AnswerUserInputAsync(
        UserInputRequest question, CodingAgentRequest request,
        Func<AgentChatEntry, CancellationToken, Task> onChatEntry, CancellationToken ct)
    {
        if (request.Interaction is not { } interaction)
            return new UserInputResponse { Answer = "", WasFreeform = true };

        var options = (question.Choices ?? [])
            .Where(c => c is { Length: > 0 })
            .Select(c => new AgentQuestionOption(c, c))
            .ToList();
        var answer = await interaction.AskAsync(new AgentQuestion(
            question.Question ?? "The agent asks for input.",
            options, MultiSelect: false,
            AllowFreeText: question.AllowFreeform ?? true), ct);
        await onChatEntry(new AgentChatEntry(
            AgentChatRole.System, "The operator answered the agent's question.",
            _time.GetUtcNow(), Label: "Question"), ct);
        var picked = answer.SelectedIds.FirstOrDefault();
        return new UserInputResponse
        {
            Answer = picked ?? answer.FreeText ?? "",
            WasFreeform = picked is null,
        };
    }

    /// <summary>
    /// Multi-turn idle wait: the next guidance text becomes the next turn's prompt; a fired
    /// end token resolves to null (the operator ended the idle session).
    /// </summary>
    private static async Task<string?> WaitForNextTurnAsync(
        IAgentInteraction interaction, CancellationToken endToken, CancellationToken sessionToken)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(endToken, sessionToken);
        try
        {
            return await interaction.WaitForGuidanceAsync(either.Token);
        }
        catch (OperationCanceledException) when (
            endToken.IsCancellationRequested && !sessionToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Operator guidance arrives as additional messages into the live session.</summary>
    private static async Task PumpGuidanceAsync(
        CopilotSession session, IAgentInteraction interaction, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var guidance = await interaction.WaitForGuidanceAsync(ct);
            await session.SendAsync(guidance, ct);
        }
    }

    /// <summary>Live setting changes: policies swap in place; a model change maps to SetModelAsync.</summary>
    private async Task PumpSettingsAsync(
        CopilotSession session, IAgentInteraction interaction, SessionState settings,
        Func<AgentChatEntry, CancellationToken, Task> onChatEntry, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var setting = await interaction.WaitForSettingAsync(ct);
            switch (setting.Key.ToLowerInvariant())
            {
                case AgentSettingKeys.PermissionMode:
                    settings.PermissionMode = setting.Value;
                    break;
                case AgentSettingKeys.PushPolicy:
                    settings.PushPolicy = setting.Value;
                    break;
                case AgentSettingKeys.Model:
                    // A model switch carries the last chosen reasoning effort along.
                    settings.Model = setting.Value;
                    await session.SetModelAsync(setting.Value,
                        new SetModelOptions { ReasoningEffort = settings.ReasoningEffort }, ct);
                    break;
                case AgentSettingKeys.ReasoningEffort:
                    // Effort rides SetModelAsync — applied now when a model was chosen this
                    // session, otherwise remembered for the next model switch.
                    settings.ReasoningEffort = setting.Value;
                    if (settings.Model is { Length: > 0 } model)
                        await session.SetModelAsync(model,
                            new SetModelOptions { ReasoningEffort = setting.Value }, ct);
                    break;
                default:
                    continue;
            }
            await onChatEntry(new AgentChatEntry(
                AgentChatRole.System, $"Session setting '{setting.Key}' changed to '{setting.Value}' by the operator.",
                _time.GetUtcNow(), Label: "Session settings"), ct);
        }
    }

    private sealed class SessionState(string permissionMode, string pushPolicy)
    {
        public volatile string PermissionMode = permissionMode;
        public volatile string PushPolicy = pushPolicy;

        /// <summary>The model the operator chose this session; null until the first switch.</summary>
        public volatile string? Model;

        /// <summary>The chosen reasoning effort; rides along with every model switch.</summary>
        public volatile string? ReasoningEffort;
    }
}

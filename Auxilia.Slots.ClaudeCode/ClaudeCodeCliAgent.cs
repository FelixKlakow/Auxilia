using System.Diagnostics;
using System.Text.Json;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Views;

namespace Auxilia.Slots.ClaudeCode;

/// <summary>
/// Runs the Claude Code CLI headless (`--output-format stream-json`) in the run's workspace and
/// streams every transcript event as a chat entry. The credential (account token or API key)
/// travels exclusively via the process environment — never on the command line, never in any
/// error message or chat entry.
/// <para>
/// With an <see cref="IAgentInteraction"/> present the session is INTERACTIVE
/// (`--input-format stream-json`, permission prompts over the stdio control protocol): the CLI's
/// <c>can_use_tool</c> control requests become operator questions, the answers go back as
/// <c>control_response</c> lines, and operator guidance is injected as additional user turns.
/// Without interaction the session is autonomous with permissions skipped — the container is
/// the sandbox.
/// </para>
/// </summary>
public sealed class ClaudeCodeCliAgent(
    ClaudeCodeCliOptions options,
    IClaudeCliProcessFactory? processFactory = null,
    TimeProvider? time = null) : ICodingAgent
{
    private const int StderrTailLength = 1000;

    private readonly IClaudeCliProcessFactory _processFactory = processFactory ?? new ClaudeCliProcessFactory();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<CodingAgentResult> RunAsync(
        CodingAgentRequest request,
        Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
        CancellationToken cancellationToken = default)
    {
        using var process = _processFactory.Start(BuildStartInfo(request));
        using var stdinGate = new SemaphoreSlim(1, 1);
        using var sessionScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Mutable per-session state: the operator can retune it live over the setting channel.
        var permissionMode = new SessionSettings(request.PermissionMode, request.PushPolicy);
        var planTracker = new PlanTracker();
        Task? guidanceTask = null;
        Task? settingsTask = null;
        try
        {
            // Drain stderr concurrently so a chatty CLI can't dead-lock on a full pipe.
            var stderrTask = process.Error.ReadToEndAsync(cancellationToken);

            if (request.Interaction is { } interaction)
            {
                // The instruction is the first user turn; stdin stays open for control
                // responses and operator guidance until the session finishes.
                await WriteUserMessageAsync(process, stdinGate, request.Instruction, cancellationToken);
                guidanceTask = PumpGuidanceAsync(process, stdinGate, interaction, sessionScope.Token);
                settingsTask = PumpSettingsAsync(
                    process, stdinGate, interaction, permissionMode, onChatEntry, sessionScope.Token);
            }

            var parser = new ClaudeStreamJsonParser();
            while (await process.Output.ReadLineAsync(cancellationToken) is { } line)
            {
                if (request.Interaction is { } steering && TryParseControlRequest(line) is { } control)
                {
                    // TodoWrite is bookkeeping, not a permission: auto-approved in every mode
                    // (never a decision card), and its todos become the published plan.
                    if (string.Equals(control.ToolName, "TodoWrite", StringComparison.OrdinalIgnoreCase))
                    {
                        if (request.OnPlanUpdate is { } publishPlan
                            && TryParseTodos(control.InputJson) is { Count: > 0 } todos)
                            await publishPlan(todos, cancellationToken);
                        await WriteLineAsync(process, stdinGate, AllowResponse(control.RequestId), cancellationToken);
                        continue;
                    }

                    await AnswerControlRequestAsync(
                        process, stdinGate, steering, control, permissionMode,
                        onChatEntry, cancellationToken);
                    continue;
                }

                // Plan revisions stream as assistant tool calls: TodoWrite (full snapshots) on
                // older CLIs, TaskCreate/TaskUpdate (incremental) on current ones — the tracker
                // folds both into full snapshots.
                if (request.OnPlanUpdate is { } onPlan
                    && planTracker.ApplyLine(line) is { Count: > 0 } snapshot)
                    await onPlan(snapshot, cancellationToken);

                foreach (var entry in parser.ParseLine(line, _time.GetUtcNow()))
                    await onChatEntry(entry, cancellationToken);

                // The result event ends the conversation — closing stdin lets the CLI exit.
                if (parser.Result is not null && request.Interaction is not null)
                {
                    await sessionScope.CancelAsync();
                    process.CloseInput();
                }
            }

            var exitCode = await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;

            if (parser.Result is not { } result)
                return new CodingAgentResult(
                    Success: false,
                    Summary: null,
                    ErrorMessage: $"The Claude Code CLI exited with code {exitCode} without a result event."
                                  + StderrSuffix(stderr));

            var success = !result.IsError && exitCode == 0;
            return new CodingAgentResult(
                success,
                result.ResultText,
                result.NumTurns,
                result.TotalCostUsd,
                result.DurationMs,
                success
                    ? null
                    : (result.ResultText is { Length: > 0 }
                          ? result.ResultText
                          : $"The Claude Code CLI reported '{result.Subtype}' (exit code {exitCode}).")
                      + StderrSuffix(stderr));
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
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

    /// <summary>Mutable session state shared between the output loop and the setting pump.</summary>
    private sealed class SessionSettings(string permissionMode, string pushPolicy)
    {
        public volatile string PermissionMode = permissionMode;

        /// <summary>The PUSH action kind has its own policy, independent of the global mode.</summary>
        public volatile string PushPolicy = pushPolicy;
    }

    /// <summary>
    /// Applies live operator setting changes: permission mode switches take effect for the NEXT
    /// permission request; a model change is forwarded to the CLI as a set_model control request.
    /// </summary>
    private async Task PumpSettingsAsync(
        IClaudeCliProcess process, SemaphoreSlim stdinGate, IAgentInteraction interaction,
        SessionSettings settings, Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var setting = await interaction.WaitForSettingAsync(ct);
            switch (setting.Key.ToLowerInvariant())
            {
                case AgentSettingKeys.PermissionMode:
                    settings.PermissionMode = setting.Value;
                    await onChatEntry(new AgentChatEntry(
                        AgentChatRole.System, $"Permission mode changed to '{setting.Value}' by the operator.",
                        _time.GetUtcNow(), Label: "Session settings"), ct);
                    break;

                case AgentSettingKeys.PushPolicy:
                    settings.PushPolicy = setting.Value;
                    await onChatEntry(new AgentChatEntry(
                        AgentChatRole.System, $"Push policy changed to '{setting.Value}' by the operator.",
                        _time.GetUtcNow(), Label: "Session settings"), ct);
                    break;

                case AgentSettingKeys.Model:
                    await WriteLineAsync(process, stdinGate, JsonSerializer.Serialize(new
                    {
                        type = "control_request",
                        request_id = $"set-model-{Guid.NewGuid():N}",
                        request = new { subtype = "set_model", model = setting.Value }
                    }), ct);
                    await onChatEntry(new AgentChatEntry(
                        AgentChatRole.System, $"Model switched to '{setting.Value}' by the operator.",
                        _time.GetUtcNow(), Label: "Session settings"), ct);
                    break;

                default:
                    await onChatEntry(new AgentChatEntry(
                        AgentChatRole.System, $"Ignored unknown session setting '{setting.Key}'.",
                        _time.GetUtcNow(), Label: "Session settings"), ct);
                    break;
            }
        }
    }

    internal ProcessStartInfo BuildStartInfo(CodingAgentRequest request)
    {
        var interactive = request.Interaction is not null;
        var startInfo = new ProcessStartInfo(options.CliPath)
        {
            WorkingDirectory = request.WorkspaceDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = interactive,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-p");
        if (interactive)
        {
            // The instruction arrives as the first stream-json user message on stdin, which
            // stays open for permission answers and operator guidance.
            startInfo.ArgumentList.Add("--input-format");
            startInfo.ArgumentList.Add("stream-json");
        }
        else
        {
            startInfo.ArgumentList.Add(request.Instruction);
        }
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--verbose");
        if (options.MaxTurns is { } maxTurns)
        {
            startInfo.ArgumentList.Add("--max-turns");
            startInfo.ArgumentList.Add(maxTurns.ToString());
        }
        if (interactive)
        {
            // Permission prompts ride the stdio control protocol and are decided by the operator.
            startInfo.ArgumentList.Add("--permission-prompt-tool");
            startInfo.ArgumentList.Add("stdio");
        }
        else
        {
            // The workflow container IS the sandbox (isolated network, scoped workspace) —
            // interactive permission prompts cannot be answered in a headless run.
            startInfo.ArgumentList.Add("--dangerously-skip-permissions");
        }
        if (options.Model is { Length: > 0 } model)
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
        }

        // Account token first, API key as the fallback — only the credential in use is
        // exported, and only through the environment.
        if (options.OAuthToken is { Length: > 0 } oauthToken)
            startInfo.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = oauthToken;
        else if (options.ApiKey is { Length: > 0 } apiKey)
            startInfo.Environment["ANTHROPIC_API_KEY"] = apiKey;
        // Keep egress within the workflow's declared allowlist: inference only, no
        // telemetry, error reporting, or auto-update traffic.
        startInfo.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";
        // The CLI refuses --dangerously-skip-permissions as root unless told it runs
        // sandboxed — which it does: isolated container, default-deny egress, per-run workspace.
        startInfo.Environment["IS_SANDBOX"] = "1";
        return startInfo;
    }

    /// <summary>A <c>can_use_tool</c> control request, when the line is one.</summary>
    internal static ControlRequest? TryParseControlRequest(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("control_request", StringComparison.Ordinal))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("type", out var t) ? t.GetString() : null) != "control_request"
                || !root.TryGetProperty("request_id", out var idProperty)
                || idProperty.GetString() is not { Length: > 0 } requestId
                || !root.TryGetProperty("request", out var request)
                || (request.TryGetProperty("subtype", out var s) ? s.GetString() : null) != "can_use_tool")
                return null;

            var toolName = request.TryGetProperty("tool_name", out var name)
                ? name.GetString() ?? "tool"
                : "tool";
            var inputJson = request.TryGetProperty("input", out var input)
                ? input.GetRawText()
                : null;
            // The CLI may propose standing permission changes (allow-always rules, mode
            // switches) — carried verbatim so a picked one goes back as updatedPermissions.
            var suggestions = new List<string>();
            if (request.TryGetProperty("permission_suggestions", out var proposed)
                && proposed.ValueKind == JsonValueKind.Array)
                suggestions.AddRange(proposed.EnumerateArray().Select(s => s.GetRawText()));
            return new ControlRequest(requestId, toolName, inputJson, suggestions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task AnswerControlRequestAsync(
        IClaudeCliProcess process, SemaphoreSlim stdinGate, IAgentInteraction interaction,
        ControlRequest control, SessionSettings settings,
        Func<AgentChatEntry, CancellationToken, Task> onChatEntry, CancellationToken ct)
    {
        // AskUserQuestion is a QUESTION, not a permission: its answers must ride back inside
        // updatedInput.answers (a bare allow runs the tool answerless — "the user did not
        // answer"). It therefore always surfaces to the operator, in EVERY permission mode.
        if (string.Equals(control.ToolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase)
            && control.InputJson is { Length: > 0 })
        {
            await AnswerUserQuestionsAsync(process, stdinGate, interaction, control, onChatEntry, ct);
            return;
        }

        // Per-action policy: a git push is governed by ITS policy, everything else by the
        // global mode — "auto-approve edits but ask before each push" and the inverse both work.
        var isPush = IsGitPush(control.ToolName, control.InputJson);
        var effectivePolicy = isPush ? settings.PushPolicy : settings.PermissionMode;

        // Auto mode: the session stays steerable (guidance, halt), but permission requests are
        // approved without an operator round-trip — the container is still the sandbox.
        if (string.Equals(effectivePolicy, AgentPermissionModes.AutoAllow, StringComparison.OrdinalIgnoreCase))
        {
            await WriteLineAsync(process, stdinGate, AllowResponse(control.RequestId), ct);
            await onChatEntry(new AgentChatEntry(
                AgentChatRole.System,
                isPush
                    ? "A git push was allowed automatically (push policy: auto-allow)."
                    : $"Tool '{control.ToolName}' was allowed automatically (permission mode: auto-allow).",
                _time.GetUtcNow(), Label: "Permission"), ct);
            return;
        }

        // A permission decision, not a generic form: the tool input renders as a monospace
        // detail block, and the CLI's own permission suggestions (allow-always rules, mode
        // switches) become selectable outcomes beside plain allow/deny. No free-text — nuance
        // goes through the guidance channel.
        var options = new List<AgentQuestionOption> { new("allow", "Allow once") };
        for (var i = 0; i < control.Suggestions.Count; i++)
            options.Add(new AgentQuestionOption(
                $"suggestion:{i}", DescribeSuggestion(control.Suggestions[i]),
                "Also applies this standing permission for the rest of the session."));
        options.Add(new AgentQuestionOption("deny", "Deny"));

        var answer = await interaction.AskAsync(new AgentQuestion(
            isPush
                ? "The agent wants to PUSH to the remote repository. Allow it?"
                : $"The agent asks to use the tool '{control.ToolName}'. Allow it?",
            options, MultiSelect: false, AllowFreeText: false,
            Detail: PrettyJson(control.InputJson)), ct);

        var picked = answer.SelectedIds.FirstOrDefault() ?? "deny";
        var suggestion = picked.StartsWith("suggestion:", StringComparison.Ordinal)
                         && int.TryParse(picked["suggestion:".Length..], out var index)
                         && index < control.Suggestions.Count
            ? control.Suggestions[index]
            : null;
        var allowed = suggestion is not null
                      || string.Equals(picked, "allow", StringComparison.OrdinalIgnoreCase);
        var response = allowed
            ? AllowResponse(control.RequestId, suggestion)
            : JsonSerializer.Serialize(new
            {
                type = "control_response",
                response = new
                {
                    subtype = "success",
                    request_id = control.RequestId,
                    response = new
                    {
                        behavior = "deny",
                        message = answer.FreeText is { Length: > 0 } why ? why : "Denied by the operator."
                    }
                }
            });
        await WriteLineAsync(process, stdinGate, response, ct);
        await onChatEntry(new AgentChatEntry(
            AgentChatRole.System,
            $"Tool '{control.ToolName}' was {(allowed ? "allowed" : "denied")} by the operator"
            + (suggestion is not null ? $" ({DescribeSuggestion(suggestion)})" : "") + ".",
            _time.GetUtcNow(), Label: "Permission"), ct);
    }

    /// <summary>
    /// Surfaces the CLI's AskUserQuestion to the operator (one form per question, options from
    /// the tool input, free text always possible) and returns the answers the documented way:
    /// allow + updatedInput carrying the ORIGINAL questions plus an answers map keyed by the
    /// question text, valued with the selected label(s) or the typed text.
    /// </summary>
    private async Task AnswerUserQuestionsAsync(
        IClaudeCliProcess process, SemaphoreSlim stdinGate, IAgentInteraction interaction,
        ControlRequest control, Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
        CancellationToken ct)
    {
        System.Text.Json.Nodes.JsonArray questions;
        try
        {
            questions = System.Text.Json.Nodes.JsonNode.Parse(control.InputJson!)?["questions"]
                ?.AsArray() ?? [];
        }
        catch (JsonException)
        {
            questions = [];
        }

        var answers = new System.Text.Json.Nodes.JsonObject();
        foreach (var node in questions)
        {
            if (node?["question"]?.GetValue<string>() is not { Length: > 0 } text)
                continue;
            var multiSelect = node["multiSelect"]?.GetValue<bool>() ?? false;
            // The answers map is keyed by question text and valued by option LABEL — so the
            // label doubles as the option id.
            var options = new List<AgentQuestionOption>();
            foreach (var optionNode in node["options"]?.AsArray() ?? [])
                if (optionNode?["label"]?.GetValue<string>() is { Length: > 0 } label)
                    options.Add(new AgentQuestionOption(
                        label, label, optionNode["description"]?.GetValue<string>()));

            var answer = await interaction.AskAsync(
                new AgentQuestion(text, options, multiSelect, AllowFreeText: true), ct);
            if (answer.SelectedIds.Count > 0)
                answers[text] = multiSelect
                    ? new System.Text.Json.Nodes.JsonArray(
                        answer.SelectedIds.Select(s => (System.Text.Json.Nodes.JsonNode)s).ToArray())
                    : answer.SelectedIds[0];
            else if (answer.FreeText is { Length: > 0 } freeText)
                answers[text] = freeText;
        }

        var response = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new System.Text.Json.Nodes.JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = control.RequestId,
                ["response"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["behavior"] = "allow",
                    ["updatedInput"] = new System.Text.Json.Nodes.JsonObject
                    {
                        // The tool re-reads its questions from the (updated) input — pass them through.
                        ["questions"] = questions.DeepClone(),
                        ["answers"] = answers,
                    },
                },
            },
        };
        await WriteLineAsync(process, stdinGate, response.ToJsonString(), ct);
        await onChatEntry(new AgentChatEntry(
            AgentChatRole.System,
            $"The operator answered {answers.Count} of {questions.Count} question(s).",
            _time.GetUtcNow(), Label: "Question"), ct);
    }

    /// <summary>
    /// Folds the CLI's plan-shaped tool calls into full snapshots. Two vocabularies: TodoWrite
    /// (a full snapshot per call — older CLIs/SDK) and TaskCreate/TaskUpdate (incremental —
    /// current CLIs; ids are assigned 1-based in creation order, matching the CLI's numbering).
    /// </summary>
    internal sealed class PlanTracker
    {
        private readonly List<AgentPlanItem> _items = [];

        /// <summary>The updated full snapshot when the line changed the plan; null otherwise.</summary>
        public IReadOnlyList<AgentPlanItem>? ApplyLine(string line)
        {
            if (TryParseStreamedTodoWrite(line) is { Count: > 0 } todos)
            {
                _items.Clear();
                _items.AddRange(todos);
                return _items.ToList();
            }

            foreach (var (name, input) in EnumerateToolUses(line, "TaskCreate", "TaskUpdate"))
            {
                if (name == "TaskCreate")
                {
                    var subject = input.TryGetProperty("subject", out var s) ? s.GetString() : null;
                    if (subject is { Length: > 0 })
                        _items.Add(new AgentPlanItem(subject, AgentPlanStatuses.Pending));
                }
                else if (input.TryGetProperty("taskId", out var idProperty)
                         && int.TryParse(idProperty.ToString(), out var taskId)
                         && taskId >= 1 && taskId <= _items.Count
                         && input.TryGetProperty("status", out var statusProperty)
                         && statusProperty.GetString() is { Length: > 0 } status)
                {
                    _items[taskId - 1] = _items[taskId - 1] with { Status = status };
                }
                else
                {
                    continue;
                }
                return _items.ToList();
            }
            return null;
        }

        /// <summary>Matching assistant-message tool_use blocks as (name, input) pairs.</summary>
        private static IEnumerable<(string Name, JsonElement Input)> EnumerateToolUses(
            string line, params string[] names)
        {
            if (!names.Any(n => line.Contains(n, StringComparison.Ordinal)))
                yield break;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                yield break;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || (root.TryGetProperty("type", out var t) ? t.GetString() : null) != "assistant"
                    || !root.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                    yield break;
                foreach (var block in content.EnumerateArray())
                    if ((block.TryGetProperty("type", out var bt) ? bt.GetString() : null) == "tool_use"
                        && (block.TryGetProperty("name", out var n) ? n.GetString() : null) is { } name
                        && names.Contains(name, StringComparer.Ordinal)
                        && block.TryGetProperty("input", out var input))
                        yield return (name, input.Clone());
            }
        }
    }

    /// <summary>The todos of a TodoWrite input: <c>{"todos":[{"content":…,"status":…}]}</c>.</summary>
    internal static IReadOnlyList<AgentPlanItem>? TryParseTodos(string? inputJson)
    {
        if (inputJson is not { Length: > 0 })
            return null;
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (!doc.RootElement.TryGetProperty("todos", out var todos)
                || todos.ValueKind != JsonValueKind.Array)
                return null;
            return todos.EnumerateArray()
                .Select(t => new AgentPlanItem(
                    t.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "",
                    t.TryGetProperty("status", out var status)
                        ? status.GetString() ?? AgentPlanStatuses.Pending
                        : AgentPlanStatuses.Pending))
                .Where(item => item.Content.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Todos of an assistant-message TodoWrite tool_use block, when the line is one.</summary>
    internal static IReadOnlyList<AgentPlanItem>? TryParseStreamedTodoWrite(string line)
    {
        if (!line.Contains("TodoWrite", StringComparison.Ordinal))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("type", out var t) ? t.GetString() : null) != "assistant"
                || !root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var block in content.EnumerateArray())
                if ((block.TryGetProperty("type", out var bt) ? bt.GetString() : null) == "tool_use"
                    && (block.TryGetProperty("name", out var name) ? name.GetString() : null) == "TodoWrite"
                    && block.TryGetProperty("input", out var input))
                    return TryParseTodos(input.GetRawText());
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A Bash tool call whose command runs <c>git … push</c> — the PUSH action kind.</summary>
    internal static bool IsGitPush(string toolName, string? inputJson)
    {
        if (!string.Equals(toolName, "Bash", StringComparison.OrdinalIgnoreCase)
            || inputJson is not { Length: > 0 })
            return false;
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var command = doc.RootElement.TryGetProperty("command", out var c) ? c.GetString() : null;
            return command is { Length: > 0 }
                   && System.Text.RegularExpressions.Regex.IsMatch(
                       command, @"\bgit\b[^|;&]*\bpush\b", System.Text.RegularExpressions.RegexOptions.Singleline);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A human label for one CLI permission suggestion; falls back to compact JSON.</summary>
    internal static string DescribeSuggestion(string suggestionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(suggestionJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "setMode" when root.TryGetProperty("mode", out var mode):
                    return $"Allow and switch to '{mode.GetString()}' mode";
                case "addRules" when root.TryGetProperty("rules", out var rules)
                                     && rules.ValueKind == JsonValueKind.Array:
                    var parts = rules.EnumerateArray().Select(rule =>
                    {
                        var tool = rule.TryGetProperty("toolName", out var n) ? n.GetString() : null;
                        var content = rule.TryGetProperty("ruleContent", out var c) ? c.GetString() : null;
                        return content is { Length: > 0 } ? $"{tool}({content})" : tool;
                    }).Where(p => p is { Length: > 0 });
                    return $"Always allow {string.Join(", ", parts)}";
            }
        }
        catch (JsonException)
        {
            // fall through to the raw form
        }
        return $"Allow with {Truncate(suggestionJson, 120)}";
    }

    /// <summary>Indented rendering of the tool input for the permission card's detail block.</summary>
    internal static string? PrettyJson(string? json)
    {
        if (json is not { Length: > 0 } || json == "{}")
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
        }
        catch (JsonException)
        {
            return Truncate(json, 2000);
        }
    }

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    /// <summary>An allow response; a picked suggestion rides along as updatedPermissions.</summary>
    private static string AllowResponse(string requestId, string? suggestionJson = null)
    {
        if (suggestionJson is null)
            return JsonSerializer.Serialize(new
            {
                type = "control_response",
                response = new
                {
                    subtype = "success",
                    request_id = requestId,
                    response = new { behavior = "allow" }
                }
            });
        using var suggestion = JsonDocument.Parse(suggestionJson);
        return JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = new
                {
                    behavior = "allow",
                    updatedPermissions = new[] { suggestion.RootElement }
                }
            }
        });
    }

    /// <summary>Forwards every operator guidance as an additional user turn on the CLI's stdin.</summary>
    private static async Task PumpGuidanceAsync(
        IClaudeCliProcess process, SemaphoreSlim stdinGate, IAgentInteraction interaction, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var guidance = await interaction.WaitForGuidanceAsync(ct);
            await WriteUserMessageAsync(process, stdinGate, guidance, ct);
        }
    }

    private static Task WriteUserMessageAsync(
        IClaudeCliProcess process, SemaphoreSlim stdinGate, string text, CancellationToken ct)
        => WriteLineAsync(process, stdinGate, JsonSerializer.Serialize(new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new[] { new { type = "text", text } }
            }
        }), ct);

    private static async Task WriteLineAsync(
        IClaudeCliProcess process, SemaphoreSlim stdinGate, string line, CancellationToken ct)
    {
        await stdinGate.WaitAsync(ct);
        try
        {
            await process.Input.WriteLineAsync(line.AsMemory(), ct);
            await process.Input.FlushAsync(ct);
        }
        finally
        {
            stdinGate.Release();
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private static string StderrSuffix(string stderr)
    {
        var trimmed = stderr.Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        if (trimmed.Length > StderrTailLength)
            trimmed = "…" + trimmed[^StderrTailLength..];
        return $" Stderr: {trimmed}";
    }

    /// <summary>One parsed <c>can_use_tool</c> control request from the CLI.</summary>
    internal sealed record ControlRequest(
        string RequestId, string ToolName, string? InputJson,
        IReadOnlyList<string>? PermissionSuggestions = null)
    {
        public IReadOnlyList<string> Suggestions => PermissionSuggestions ?? [];
    }
}

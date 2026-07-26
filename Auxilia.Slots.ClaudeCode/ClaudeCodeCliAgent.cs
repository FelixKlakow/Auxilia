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
        Task? guidanceTask = null;
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
            }

            var parser = new ClaudeStreamJsonParser();
            while (await process.Output.ReadLineAsync(cancellationToken) is { } line)
            {
                if (request.Interaction is { } steering && TryParseControlRequest(line) is { } control)
                {
                    await AnswerControlRequestAsync(
                        process, stdinGate, steering, control, onChatEntry, cancellationToken);
                    continue;
                }

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
            if (guidanceTask is not null)
                try { await guidanceTask; }
                catch (OperationCanceledException) { /* stopped with the session */ }
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
            return new ControlRequest(requestId, toolName, inputJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task AnswerControlRequestAsync(
        IClaudeCliProcess process, SemaphoreSlim stdinGate, IAgentInteraction interaction,
        ControlRequest control, Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
        CancellationToken ct)
    {
        var detail = control.InputJson is { Length: > 0 } input && input != "{}"
            ? $" with {Truncate(input, 300)}"
            : string.Empty;
        var answer = await interaction.AskAsync(new AgentQuestion(
            $"The agent asks to use the tool '{control.ToolName}'{detail}. Allow it?",
            [
                new AgentQuestionOption("allow", "Allow"),
                new AgentQuestionOption("deny", "Deny"),
            ],
            MultiSelect: false, AllowFreeText: true), ct);

        var allowed = answer.SelectedIds.Contains("allow", StringComparer.OrdinalIgnoreCase);
        var response = allowed
            ? JsonSerializer.Serialize(new
            {
                type = "control_response",
                response = new
                {
                    subtype = "success",
                    request_id = control.RequestId,
                    response = new { behavior = "allow" }
                }
            })
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
            $"Tool '{control.ToolName}' was {(allowed ? "allowed" : "denied")} by the operator.",
            _time.GetUtcNow(), Label: "Permission"), ct);
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
    internal sealed record ControlRequest(string RequestId, string ToolName, string? InputJson);
}

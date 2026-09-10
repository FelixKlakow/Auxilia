using System.Diagnostics;
using System.Text;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Views;

namespace Auxilia.Slots.Codex;

/// <summary>
/// Runs the OpenAI Codex CLI headless (`codex exec …`) in the run's workspace and streams its
/// plain-text progress lines as chat entries. The API key travels exclusively via the process
/// environment (<c>OPENAI_API_KEY</c>) — never on the command line, never in any error message
/// or chat entry. Operator interaction is not offered by the Codex CLI's exec mode —
/// questions/guidance stay a per-agent capability.
/// </summary>
public sealed class CodexCliAgent(
    CodexCliOptions options,
    ICliProcessFactory? processFactory = null,
    TimeProvider? time = null) : ICodingAgent
{
    private const int StderrTailLength = 1000;

    private readonly ICliProcessFactory _processFactory = processFactory ?? ProcessCliProcessFactory.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<CodingAgentResult> RunAsync(
        CodingAgentRequest request,
        Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
        CancellationToken cancellationToken = default)
    {
        if (request.MultiTurn)
            throw new InvalidOperationException(
                "Multi-turn sessions are the interactive console's domain — the Codex CLI's "
                + "exec mode is one-shot.");

        var stopwatch = Stopwatch.StartNew();
        using var process = _processFactory.Start(BuildStartInfo(request));
        try
        {
            // Drain stderr concurrently so a chatty CLI can't dead-lock on a full pipe.
            var stderrTask = process.Error.ReadToEndAsync(cancellationToken);

            string? lastLine = null;
            var lineCount = 0;
            while (await process.Output.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                lastLine = line.Trim();
                lineCount++;
                await onChatEntry(
                    new AgentChatEntry(AgentChatRole.Assistant, lastLine, _time.GetUtcNow()),
                    cancellationToken);
            }

            var exitCode = await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            stopwatch.Stop();

            var success = exitCode == 0;
            return new CodingAgentResult(
                success,
                lastLine,
                TurnCount: lineCount,
                DurationMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: success
                    ? null
                    : $"The Codex CLI exited with code {exitCode}." + StderrSuffix(stderr));
        }
        catch
        {
            // ANY failure of the run (cancellation, a bus publish that threw from the chat
            // callback, ...) must not leave the CLI editing and pushing inside the container.
            process.Kill();
            throw;
        }
    }

    internal ProcessStartInfo BuildStartInfo(CodingAgentRequest request)
    {
        // The CLI speaks UTF-8; without pinning it, Windows hosts decode the pipes with the
        // OEM codepage and every non-ASCII character reaches the views as mojibake.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo(options.CliPath)
        {
            WorkingDirectory = request.WorkspaceDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("exec");
        // The workflow container IS the sandbox (isolated network, scoped workspace) — the
        // CLI's own sandbox/approval machinery cannot be answered in a headless run.
        startInfo.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        if (options.Model is { Length: > 0 } model)
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
        }
        // No system-prompt seam in exec mode — base instructions ride the prompt itself.
        startInfo.ArgumentList.Add(request.BaseInstructions is { Length: > 0 } baseInstructions
            ? baseInstructions + "\n\n" + request.Instruction
            : request.Instruction);

        // The key is exported only through the environment, never as an argument.
        if (options.ApiKey is { Length: > 0 } apiKey)
            startInfo.Environment["OPENAI_API_KEY"] = apiKey;
        return startInfo;
    }

    private static string StderrSuffix(string stderr)
    {
        var trimmed = stderr.Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        if (trimmed.Length > StderrTailLength)
            trimmed = "…" + trimmed[^StderrTailLength..];
        return $" Stderr: {trimmed}";
    }
}

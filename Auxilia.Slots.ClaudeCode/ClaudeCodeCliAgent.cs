using System.Diagnostics;
using Auxilia.ClaudeCode.Workflow;
using Auxilia.Workflows.Views;

namespace Auxilia.Slots.ClaudeCode;

/// <summary>
/// Runs the Claude Code CLI headless (`-p --output-format stream-json`) in the run's
/// workspace and streams every transcript event as a chat entry. The credential (account
/// token or API key) travels exclusively via the process environment — never on the
/// command line, never in any error message or chat entry.
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
        try
        {
            // Drain stderr concurrently so a chatty CLI can't dead-lock on a full pipe.
            var stderrTask = process.Error.ReadToEndAsync(cancellationToken);

            var parser = new ClaudeStreamJsonParser();
            while (await process.Output.ReadLineAsync(cancellationToken) is { } line)
                foreach (var entry in parser.ParseLine(line, _time.GetUtcNow()))
                    await onChatEntry(entry, cancellationToken);

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
    }

    internal ProcessStartInfo BuildStartInfo(CodingAgentRequest request)
    {
        var startInfo = new ProcessStartInfo(options.CliPath)
        {
            WorkingDirectory = request.WorkspaceDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(request.Instruction);
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--verbose");
        startInfo.ArgumentList.Add("--max-turns");
        startInfo.ArgumentList.Add(options.MaxTurns.ToString());
        // The workflow container IS the sandbox (isolated network, scoped workspace) —
        // interactive permission prompts cannot be answered in a headless run.
        startInfo.ArgumentList.Add("--dangerously-skip-permissions");
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

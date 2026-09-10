using System.Diagnostics;
using System.Text;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Views;

namespace Auxilia.Slots.GitHubCopilot;

/// <summary>
/// Runs the GitHub Copilot CLI headless (`copilot -p … --allow-all-tools`) in the run's
/// workspace and streams its plain-text progress lines as chat entries. The token travels
/// exclusively via the process environment (<c>GH_TOKEN</c>) — never on the command line,
/// never in any error message or chat entry. Operator interaction is not offered by the
/// Copilot CLI's headless mode — questions/guidance stay a per-agent capability.
/// </summary>
public sealed class CopilotCliAgent(
    CopilotCliOptions options,
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
                "Multi-turn sessions need the Copilot SDK session protocol — enable the "
                + "provider's UseSdkSession setting; the headless CLI mode is one-shot.");

        var stopwatch = Stopwatch.StartNew();
        using var process = _processFactory.Start(BuildStartInfo(request));
        try
        {
            // Drain stderr concurrently so a chatty CLI can't dead-lock on a full pipe.
            var stderrTask = process.Error.ReadToEndAsync(cancellationToken);

            string? lastLine = null;
            var lineCount = 0;
            // Copilot has no structured plan channel — consecutive markdown-checklist lines
            // in its output ARE its plan; every extension republishes the full snapshot.
            var plan = new List<AgentPlanItem>();
            var inChecklist = false;
            while (await process.Output.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                lastLine = line.Trim();
                lineCount++;

                if (TryParseChecklistLine(lastLine) is { } item)
                {
                    if (!inChecklist)
                    {
                        plan.Clear();
                        inChecklist = true;
                    }
                    plan.Add(item);
                    if (request.OnPlanUpdate is { } publishPlan)
                        await publishPlan(plan.ToList(), cancellationToken);
                }
                else
                {
                    inChecklist = false;
                }

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
                    : $"The GitHub Copilot CLI exited with code {exitCode}."
                      + StderrSuffix(stderr));
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

        startInfo.ArgumentList.Add("-p");
        // No system-prompt seam in line mode — base instructions ride the prompt itself.
        startInfo.ArgumentList.Add(request.BaseInstructions is { Length: > 0 } baseInstructions
            ? baseInstructions + "\n\n" + request.Instruction
            : request.Instruction);
        // The workflow container IS the sandbox (isolated network, scoped workspace) —
        // interactive tool approvals cannot be answered in a headless run.
        startInfo.ArgumentList.Add("--allow-all-tools");
        startInfo.ArgumentList.Add("--no-color");
        if (options.Model is { Length: > 0 } model)
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
        }

        // The token is exported only through the environment, never as an argument.
        if (options.Token is { Length: > 0 } token)
            startInfo.Environment["GH_TOKEN"] = token;
        return startInfo;
    }

    /// <summary>A markdown checklist line: <c>- [ ] x</c> pending, <c>- [~]</c> running, <c>- [x]</c> done.</summary>
    internal static AgentPlanItem? TryParseChecklistLine(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line, @"^\s*[-*]\s*\[(?<state> |x|X|~)\]\s*(?<content>.+)$");
        if (!match.Success)
            return null;
        var status = match.Groups["state"].Value switch
        {
            "x" or "X" => AgentPlanStatuses.Completed,
            "~" => AgentPlanStatuses.InProgress,
            _ => AgentPlanStatuses.Pending,
        };
        return new AgentPlanItem(match.Groups["content"].Value.Trim(), status);
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

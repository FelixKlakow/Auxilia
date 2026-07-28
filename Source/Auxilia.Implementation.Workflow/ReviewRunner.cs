using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Views;

namespace Auxilia.Implementation.Workflow;

/// <summary>A reviewer pass's outcome, parsed from its verdict FILE (never from chat output).</summary>
public sealed record ReviewVerdict(bool Approved, string Notes)
{
    /// <summary>
    /// First line APPROVE/REVISE (case-insensitive), rest = notes. A missing or unreadable
    /// verdict counts as REVISE with an explanatory note — a silent reviewer never approves.
    /// </summary>
    public static ReviewVerdict FromFile(string path)
    {
        if (!File.Exists(path))
            return new ReviewVerdict(false, "The reviewer produced no verdict file.");
        var lines = File.ReadAllLines(path);
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        var approved = first.StartsWith("APPROVE", StringComparison.OrdinalIgnoreCase);
        return new ReviewVerdict(approved, string.Join("\n", lines).Trim());
    }
}

/// <summary>One AI review pass — headless or console, decided per run.</summary>
public interface IReviewRunner
{
    Task<ReviewVerdict> ReviewAsync(string instruction, string verdictPath, CancellationToken ct);
}

/// <summary>
/// Headless reviewer: a second agent instance in its non-interactive mode. The verdict is a
/// FILE the instruction tells it to write — the chat stream only feeds the run's views
/// (detail-tagged, default hidden).
/// </summary>
public sealed class HeadlessReviewRunner(
    ICodingAgent reviewer,
    string workspaceDirectory,
    IViewPublisher? views,
    TimeProvider time) : IReviewRunner
{
    public async Task<ReviewVerdict> ReviewAsync(string instruction, string verdictPath, CancellationToken ct)
    {
        if (File.Exists(verdictPath))
            File.Delete(verdictPath);
        await reviewer.RunAsync(
            new CodingAgentRequest(
                instruction, workspaceDirectory, Interaction: null, AgentPermissionModes.AutoAllow),
            (entry, token) => views?.PublishAsync(
                    AgentSessionApplication.ChatViewName, entry with { DetailTag = "reviewer" }, token)
                ?? Task.CompletedTask,
            ct);
        return ReviewVerdict.FromFile(verdictPath);
    }
}

/// <summary>
/// Console reviewer: a one-shot CLI run in a SECOND tmux session beside the author's (visible
/// via <c>tmux attach -t review-session</c>; the run terminal stays on the author). Completion
/// is the session's own exit — no hook dependency, so it works for every provider.
/// </summary>
public sealed class ConsoleReviewRunner(
    Func<ISessionHost> hostFactory,
    CodingAgentCredentials credentials,
    string workspaceDirectory) : IReviewRunner
{
    public const string SessionName = "review-session";

    public async Task<ReviewVerdict> ReviewAsync(string instruction, string verdictPath, CancellationToken ct)
    {
        if (File.Exists(verdictPath))
            File.Delete(verdictPath);

        // The instruction rides the session environment (never the command line) and is
        // expanded by the shell inside tmux as the CLI's one-shot prompt argument.
        var environment = new Dictionary<string, string>(credentials.ToEnvironment())
        {
            ["AUXILIA_REVIEW_INSTRUCTION"] = instruction
        };
        var unattended = credentials.UnattendedCliArguments is { Length: > 0 } arguments
            ? " " + arguments
            : "";
        var host = hostFactory();
        await host.StartAsync(
            new TerminalSessionInfo(
                workspaceDirectory,
                $"{credentials.CliPath}{unattended} -p \"$AUXILIA_REVIEW_INSTRUCTION\"",
                TerminalPort: 0)
            {
                Environment = environment,
                SessionName = SessionName,
                ServeTerminal = false,
                // The author's session must survive the reviewer's exit.
                EndsServerOnExit = false,
            }, ct);
        await host.WaitForSessionEndAsync(ct);
        return ReviewVerdict.FromFile(verdictPath);
    }
}

using System.Diagnostics;

namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// What one interactive terminal session needs: where it runs, the command it executes, the
/// container port ttyd serves on, and the session environment (the JIT-delivered credential —
/// delivered to the session with <c>tmux new-session -e</c>, never inside the command, never
/// logged).
/// </summary>
public sealed record TerminalSessionInfo(
    string WorkspaceDirectory,
    string Command,
    int TerminalPort)
{
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>();

    /// <summary>The tmux session name — distinct per concurrent session (author, reviewer).</summary>
    public string SessionName { get; init; } = "agent-session";

    /// <summary>Serve this session via ttyd (the run's web terminal). One session per run.</summary>
    public bool ServeTerminal { get; init; } = true;

    /// <summary>
    /// Chain <c>tmux kill-server</c> after the command, so the command's exit ends EVERY
    /// session (the auto-exit contract). Off for secondary sessions beside the main one.
    /// </summary>
    public bool EndsServerOnExit { get; init; } = true;
}

/// <summary>Seam between a session application and the tmux/ttyd host processes.</summary>
public interface ISessionHost
{
    /// <summary>
    /// Starts the session: the command inside a detached tmux session (so browser disconnects
    /// never kill it) and ttyd attaching viewers to it. Returns when both are up.
    /// </summary>
    Task StartAsync(TerminalSessionInfo session, CancellationToken cancellationToken);

    /// <summary>Completes when the session command exits (the tmux server dies with it).</summary>
    Task WaitForSessionEndAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Types <paramref name="text"/> into the session followed by Enter — how a DRIVEN
    /// console receives its prompts. The text is delivered verbatim (literal keys, no shell
    /// interpretation) and never logged.
    /// </summary>
    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Ends the session from outside — cancellation or the max-duration guard.</summary>
    Task ShutdownAsync();
}

/// <summary>
/// Production host: <c>tmux new-session -d '&lt;command&gt;; tmux kill-server'</c> plus
/// <c>ttyd --writable tmux attach</c>. The wrapper polls the tmux server; when the command
/// exits it kills the server, the poll ends, and the workflow continues. The credential
/// reaches the command only through this process environment.
/// </summary>
public sealed class TmuxSessionHost : ISessionHost
{
    private string _sessionName = "agent-session";
    private bool _ownsServer = true;
    private Process? _ttyd;

    public async Task StartAsync(TerminalSessionInfo session, CancellationToken cancellationToken)
    {
        _sessionName = session.SessionName;
        _ownsServer = session.EndsServerOnExit;
        await RunAsync(BuildTmuxStartInfo(session), cancellationToken);

        if (!session.ServeTerminal)
            return;
        var ttyd = new ProcessStartInfo { FileName = "ttyd", UseShellExecute = false };
        foreach (var argument in new[]
                 {
                     "--writable", "--port", session.TerminalPort.ToString(),
                     "tmux", "attach", "-t", session.SessionName
                 })
            ttyd.ArgumentList.Add(argument);
        _ttyd = Process.Start(ttyd) ?? throw new InvalidOperationException("Failed to start ttyd.");
    }

    /// <summary>
    /// Every variable in <see cref="TerminalSessionInfo.Environment"/> goes to THIS session
    /// with <c>-e KEY=VALUE</c> (tmux ≥ 3.2; the workflow images ship 3.3+). Only the first
    /// session forks the server and inherits the client's environment — a secondary session
    /// (the reviewer console) would otherwise run on the author's credential — so the client
    /// environment is deliberately not relied on. The command itself is ONE argv element
    /// (tmux runs it through the shell), so it may contain quotes and shell expansions;
    /// '; tmux kill-server' makes the command's exit tear down the server — the auto-exit
    /// contract.
    /// </summary>
    public static ProcessStartInfo BuildTmuxStartInfo(TerminalSessionInfo session)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tmux",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("new-session");
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(session.SessionName);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(session.WorkspaceDirectory);
        foreach (var (key, value) in session.Environment)
        {
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add($"{key}={value}");
        }
        startInfo.ArgumentList.Add(
            session.EndsServerOnExit ? $"{session.Command}; tmux kill-server" : session.Command);
        return startInfo;
    }

    /// <summary>The argv for a failure message — every <c>-e KEY=VALUE</c> value redacted.</summary>
    internal static string DescribeForLog(ProcessStartInfo startInfo)
    {
        var parts = new List<string>(startInfo.ArgumentList.Count);
        for (var i = 0; i < startInfo.ArgumentList.Count; i++)
        {
            var argument = startInfo.ArgumentList[i];
            if (argument == "-e" && i + 1 < startInfo.ArgumentList.Count)
            {
                var assignment = startInfo.ArgumentList[++i];
                var name = assignment.Split('=', 2)[0];
                parts.Add("-e");
                parts.Add($"{name}=<redacted>");
                continue;
            }
            parts.Add(argument);
        }
        return $"{startInfo.FileName} {string.Join(' ', parts)}";
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        // -l = literal keys (no key-name interpretation); Enter is its own key event so a
        // multi-line prompt arrives as ONE input. The text itself never hits a shell or log.
        var typeKeys = new ProcessStartInfo
        {
            FileName = "tmux",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "send-keys", "-t", _sessionName, "-l", text })
            typeKeys.ArgumentList.Add(argument);
        await RunAsync(typeKeys, cancellationToken);
        if (await TryRunAsync("tmux", ["send-keys", "-t", _sessionName, "Enter"], cancellationToken) != 0)
            throw new InvalidOperationException("tmux send-keys Enter failed — is the session gone?");
    }

    public async Task WaitForSessionEndAsync(CancellationToken cancellationToken)
    {
        // 'tmux has-session' exits non-zero once the server is gone.
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await TryRunAsync("tmux", ["has-session", "-t", _sessionName], cancellationToken) != 0)
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task ShutdownAsync()
    {
        // A secondary session (EndsServerOnExit=false) must never take the author's server
        // down with it — kill only its own session.
        await TryRunAsync(
            "tmux",
            _ownsServer ? ["kill-server"] : ["kill-session", "-t", _sessionName],
            CancellationToken.None);
        try
        {
            if (_ttyd is { HasExited: false })
                _ttyd.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited in between — nothing left to stop.
        }
    }

    private static async Task RunAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        if (await TryRunAsync(startInfo, ct) is var exitCode && exitCode != 0)
            throw new InvalidOperationException($"'{DescribeForLog(startInfo)}' exited with {exitCode}.");
    }

    private static Task<int> TryRunAsync(string fileName, string[] arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return TryRunAsync(startInfo, ct);
    }

    private static async Task<int> TryRunAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}

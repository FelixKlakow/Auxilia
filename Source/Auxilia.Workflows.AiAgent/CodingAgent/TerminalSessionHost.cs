using System.Diagnostics;

namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// What one interactive terminal session needs: where it runs, the command it executes, the
/// container port ttyd serves on, and the process environment (the JIT-delivered credential —
/// environment-only by contract: never arguments, never logged).
/// </summary>
public sealed record TerminalSessionInfo(
    string WorkspaceDirectory,
    string Command,
    int TerminalPort)
{
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>();
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
    private const string TmuxSessionName = "agent-session";
    private Process? _ttyd;

    public async Task StartAsync(TerminalSessionInfo session, CancellationToken cancellationToken)
    {
        await RunAsync(BuildTmuxStartInfo(session), cancellationToken);

        var ttyd = new ProcessStartInfo { FileName = "ttyd", UseShellExecute = false };
        foreach (var argument in new[]
                 {
                     "--writable", "--port", session.TerminalPort.ToString(),
                     "tmux", "attach", "-t", TmuxSessionName
                 })
            ttyd.ArgumentList.Add(argument);
        _ttyd = Process.Start(ttyd) ?? throw new InvalidOperationException("Failed to start ttyd.");
    }

    /// <summary>
    /// The tmux server inherits this process's environment — the session credential rides in
    /// via <see cref="TerminalSessionInfo.Environment"/>, never on the command line. The
    /// command itself is ONE argv element (tmux runs it through the shell), so it may contain
    /// quotes and shell expansions; '; tmux kill-server' makes the command's exit tear down
    /// the server — the auto-exit contract.
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
        foreach (var argument in new[]
                 {
                     "new-session", "-d", "-s", TmuxSessionName,
                     "-c", session.WorkspaceDirectory,
                     $"{session.Command}; tmux kill-server"
                 })
            startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in session.Environment)
            startInfo.Environment[key] = value;
        return startInfo;
    }

    public async Task WaitForSessionEndAsync(CancellationToken cancellationToken)
    {
        // 'tmux has-session' exits non-zero once the server is gone.
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await TryRunAsync("tmux", ["has-session", "-t", TmuxSessionName], cancellationToken) != 0)
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task ShutdownAsync()
    {
        await TryRunAsync("tmux", ["kill-server"], CancellationToken.None);
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
            throw new InvalidOperationException(
                $"'{startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)}' exited with {exitCode}.");
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

using System.Diagnostics;

namespace Auxilia.CodingSession.Workflow;

/// <summary>Seam between the session application and the tmux/ttyd host processes.</summary>
public interface ISessionHost
{
    /// <summary>
    /// Starts the session: the CLI inside a detached tmux session (so browser disconnects
    /// never kill it) and ttyd attaching viewers to it. Returns when both are up.
    /// </summary>
    Task StartAsync(SessionRunContext context, CancellationToken cancellationToken);

    /// <summary>Completes when the session CLI exits (the tmux server dies with it).</summary>
    Task WaitForSessionEndAsync(CancellationToken cancellationToken);

    /// <summary>Ends the session from outside — cancellation or the max-duration guard.</summary>
    Task ShutdownAsync();
}

/// <summary>
/// Production host: <c>tmux new-session -d '&lt;cli&gt;; tmux kill-server'</c> plus
/// <c>ttyd --writable tmux attach</c>. The wrapper polls the tmux server; when the CLI
/// exits it kills the server, the poll ends, and the workflow continues to result
/// collection. The OAuth token reaches the CLI only through this process environment.
/// </summary>
public sealed class TmuxSessionHost : ISessionHost
{
    private const string TmuxSessionName = "coding-session";
    private Process? _ttyd;

    public async Task StartAsync(SessionRunContext context, CancellationToken cancellationToken)
    {
        // '; tmux kill-server' makes the CLI's exit tear down the server — the auto-exit contract.
        await RunAsync("tmux",
            $"new-session -d -s {TmuxSessionName} -c \"{context.WorkspaceDirectory}\" " +
            $"\"{context.SessionCommand}; tmux kill-server\"", cancellationToken);

        _ttyd = Process.Start(new ProcessStartInfo
        {
            FileName = "ttyd",
            Arguments = $"--writable --port {context.TerminalPort} tmux attach -t {TmuxSessionName}",
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start ttyd.");
    }

    public async Task WaitForSessionEndAsync(CancellationToken cancellationToken)
    {
        // 'tmux has-session' exits non-zero once the server is gone.
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await TryRunAsync("tmux", $"has-session -t {TmuxSessionName}", cancellationToken) != 0)
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task ShutdownAsync()
    {
        await TryRunAsync("tmux", "kill-server", CancellationToken.None);
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

    private static async Task RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        if (await TryRunAsync(fileName, arguments, ct) is var exitCode && exitCode != 0)
            throw new InvalidOperationException($"'{fileName} {arguments}' exited with {exitCode}.");
    }

    private static async Task<int> TryRunAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}

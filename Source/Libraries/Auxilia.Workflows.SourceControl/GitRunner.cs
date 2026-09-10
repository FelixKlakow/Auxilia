using System.Diagnostics;

namespace Auxilia.Workflows.SourceControl;

/// <summary>Seam for a workflow's local git operations against its mounted workspace.</summary>
public interface IGitRunner
{
    /// <summary>
    /// Runs git; <c>Output</c> is stdout on success, and stdout plus stderr when git fails, so a
    /// failure message carries git's own explanation.
    /// </summary>
    Task<(int ExitCode, string Output)> RunAsync(
        string workingDirectory, string arguments, CancellationToken cancellationToken);
}

public sealed class ProcessGitRunner : IGitRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(
        string workingDirectory, string arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Failed to start git.");
        // Both pipes are drained concurrently: a git that fills stderr while stdout is being
        // read to the end would otherwise block on the full pipe and never exit.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, Combine(process.ExitCode, stdout, stderr));
    }

    internal static string Combine(int exitCode, string stdout, string stderr)
    {
        if (exitCode == 0 || string.IsNullOrWhiteSpace(stderr))
            return stdout;
        return stdout.Length == 0 || stdout.EndsWith('\n') ? stdout + stderr : stdout + "\n" + stderr;
    }
}

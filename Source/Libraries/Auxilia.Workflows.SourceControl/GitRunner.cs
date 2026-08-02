using System.Diagnostics;

namespace Auxilia.Workflows.SourceControl;

/// <summary>Seam for a workflow's local git operations against its mounted workspace.</summary>
public interface IGitRunner
{
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
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output);
    }
}

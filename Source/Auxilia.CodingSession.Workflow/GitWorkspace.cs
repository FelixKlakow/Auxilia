using System.Diagnostics;

namespace Auxilia.CodingSession.Workflow;

/// <summary>Seam for the local git operations of the session (branch, diff — never push).</summary>
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

/// <summary>
/// LOCAL-ONLY git around the Workspace-Manager-mounted repository: session branch at start,
/// changed-file names at the end. Pushing stays the Workspace Manager's job — this container
/// never holds remote credentials.
/// </summary>
public sealed class GitWorkspace(IGitRunner git, string workingDirectory)
{
    private string? _startCommit;

    public async Task StartSessionBranchAsync(string branchName, CancellationToken ct)
    {
        var (exitCode, output) = await git.RunAsync(workingDirectory, "rev-parse HEAD", ct);
        _startCommit = exitCode == 0 ? output.Trim() : null;
        await git.RunAsync(workingDirectory, $"checkout -b {branchName}", ct);
    }

    /// <summary>Committed changes since the session started plus anything left uncommitted.</summary>
    public async Task<IReadOnlyList<string>> ChangedFilesAsync(CancellationToken ct)
    {
        var files = new List<string>();
        if (_startCommit is not null)
        {
            var (exitCode, output) = await git.RunAsync(
                workingDirectory, $"diff --name-only {_startCommit} HEAD", ct);
            if (exitCode == 0)
                files.AddRange(Lines(output));
        }

        // Porcelain lines are "XY <path>" — the two status chars and separator must be cut
        // from the RAW line (trimming first would eat a leading space of the status field).
        var (statusExit, statusOutput) = await git.RunAsync(workingDirectory, "status --porcelain", ct);
        if (statusExit == 0)
            files.AddRange(statusOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 3)
                .Select(line => line[3..].Trim()));

        return files.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> Lines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

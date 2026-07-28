using Auxilia.Workflows.SourceControl;

namespace Auxilia.CodingSession.Workflow;

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
        Directory.CreateDirectory(workingDirectory);
        var (exitCode, output) = await git.RunAsync(workingDirectory, "rev-parse HEAD", ct);
        if (exitCode != 0)
        {
            // No repository (or no commits) at the mounted path — initialize one so the session
            // has a real branch to work on and diffs are meaningful. A real clone skips this.
            await git.RunAsync(workingDirectory, "init", ct);
            await git.RunAsync(workingDirectory, "config user.email session@auxilia.local", ct);
            await git.RunAsync(workingDirectory, "config user.name \"Auxilia Session\"", ct);
            await git.RunAsync(workingDirectory, "commit --allow-empty -m \"session start\"", ct);
            (exitCode, output) = await git.RunAsync(workingDirectory, "rev-parse HEAD", ct);
        }

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

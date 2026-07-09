using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Prepares per-run repository workspaces (ARCHITECTURE §9). Cached repositories are cloned
/// once into the warm cache, refreshed via <c>git fetch</c> on reuse, and copied into an
/// isolated per-run directory the launcher bind-mounts at <c>/workspace</c>. The copy is a
/// plain recursive copy — CoW snapshots on non-overlayfs hosts are a documented follow-up.
/// <c>NoCache</c> repositories are cloned straight into the run directory and never touch
/// the cache.
/// </summary>
public sealed class WorkspaceManager(
    IOptions<WorkflowDispatcherSettings> settings,
    ILogger<WorkspaceManager> logger)
{
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates the run workspace and returns its root, or null when no repositories are declared.
    /// </summary>
    public async Task<string?> PrepareAsync(
        Guid instanceId, IReadOnlyList<RepositoryDeclaration> repositories, CancellationToken ct = default)
    {
        if (repositories.Count == 0)
            return null;

        var runRoot = RunRootFor(instanceId);
        foreach (var repository in repositories)
        {
            var runRepoDir = Path.Combine(runRoot, "repos", repository.Id);
            Directory.CreateDirectory(runRepoDir);

            if (repository.NoCache)
            {
                // High-sensitivity path: fetched fresh per run, never enters the warm cache.
                await CloneAsync(repository, runRepoDir, ct);
            }
            else
            {
                var cacheDir = await EnsureWarmCacheAsync(repository, ct);
                CopyDirectory(cacheDir, runRepoDir);
            }

            logger.LogInformation(
                "Workspace repository prepared. InstanceId={InstanceId} RepositoryId={RepositoryId} CloneUrl={CloneUrl} NoCache={NoCache}",
                instanceId, repository.Id, StripUserInfo(repository.CloneUrl), repository.NoCache);
        }

        return runRoot;
    }

    /// <summary>Best-effort removal of the run's workspace root.</summary>
    public Task CleanupAsync(Guid instanceId)
    {
        var runRoot = RunRootFor(instanceId);
        try
        {
            if (Directory.Exists(runRoot))
                DeleteDirectory(runRoot);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex,
                "Failed to delete workspace {RunRoot} — leaving it for manual cleanup.", runRoot);
        }
        return Task.CompletedTask;
    }

    private string RunRootFor(Guid instanceId)
        => Path.Combine(settings.Value.WorkspaceRootDirectory, instanceId.ToString("N"));

    /// <summary>
    /// Clones the repository into the warm cache on first use; refreshes an existing entry
    /// via <c>git fetch</c>. A failed fetch logs a warning and proceeds with the stale cache —
    /// availability over freshness.
    /// </summary>
    private async Task<string> EnsureWarmCacheAsync(RepositoryDeclaration repository, CancellationToken ct)
    {
        var cacheDir = Path.Combine(settings.Value.WarmCacheDirectory, CacheKeyFor(repository.CloneUrl));
        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(settings.Value.WarmCacheDirectory);
            await CloneAsync(repository, cacheDir, ct);
            return cacheDir;
        }

        try
        {
            await RunGitAsync(["-C", cacheDir, "fetch", "--all", "--prune"], repository.CloneUrl, ct);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex,
                "Warm-cache fetch failed for repository {RepositoryId} ({CloneUrl}) — proceeding with the stale cache.",
                repository.Id, StripUserInfo(repository.CloneUrl));
        }
        return cacheDir;
    }

    private static async Task CloneAsync(RepositoryDeclaration repository, string targetDirectory, CancellationToken ct)
    {
        var args = new List<string> { "clone", repository.CloneUrl };
        if (repository.Branch is not null)
        {
            args.Add("--branch");
            args.Add(repository.Branch);
        }
        args.Add(targetDirectory);

        await RunGitAsync(args, repository.CloneUrl, ct);

        // A tokened clone URL would otherwise sit in the copy's .git/config and ride into the
        // workflow container — workflows never hold raw credentials (ARCHITECTURE §8).
        var stripped = StripUserInfo(repository.CloneUrl);
        if (!string.Equals(stripped, repository.CloneUrl, StringComparison.Ordinal))
            await RunGitAsync(
                ["-C", targetDirectory, "remote", "set-url", "origin", stripped],
                repository.CloneUrl, ct);
    }

    /// <summary>
    /// Runs one git command with redirected output, a hard per-command timeout, and an empty
    /// credential helper so git never prompts. Non-zero exit throws with the (redacted) stderr.
    /// </summary>
    private static async Task RunGitAsync(
        IReadOnlyList<string> args, string cloneUrl, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("credential.helper=");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start git process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var commandName = args[0] == "-C" ? args[2] : args[0];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(GitCommandTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"git {commandName} for {StripUserInfo(cloneUrl)} exceeded the {GitCommandTimeout.TotalMinutes:0}-minute timeout.");
        }

        await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {commandName} for {StripUserInfo(cloneUrl)} failed (exit {process.ExitCode}): {Redact(stderr, cloneUrl)}");
    }

    /// <summary>Cache directory name: first 16 hex chars of the clone URL's SHA-256.</summary>
    internal static string CacheKeyFor(string cloneUrl)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cloneUrl)))[..16].ToLowerInvariant();

    /// <summary>Strips userinfo (credentials) from a clone URL for logging.</summary>
    internal static string StripUserInfo(string cloneUrl)
        => Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)
            ? uri.GetComponents(UriComponents.Scheme | UriComponents.HostAndPort | UriComponents.PathAndQuery,
                UriFormat.UriEscaped)
            : cloneUrl;

    private static string Redact(string text, string cloneUrl)
        => text.Replace(cloneUrl, StripUserInfo(cloneUrl));

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, file)), overwrite: true);
    }

    /// <summary>Deletes recursively, clearing read-only attributes git sets on object files.</summary>
    private static void DeleteDirectory(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}

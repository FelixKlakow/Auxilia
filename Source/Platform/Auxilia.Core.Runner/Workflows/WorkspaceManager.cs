using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Prepares per-run workspaces (ARCHITECTURE §9). Cached repositories are cloned once into
/// the warm cache, refreshed via <c>git fetch</c> on reuse (the checkout advanced to the
/// fetched tip of the requested branch), and copied into an isolated per-run directory the
/// launcher bind-mounts at <c>/workspace</c>. The copy is a plain recursive copy — CoW
/// snapshots on non-overlayfs hosts are a documented follow-up. The cache is keyed by the
/// credential-stripped URL; a clone credential is injected per git command
/// (<c>-c url.&lt;tokened&gt;.insteadOf=&lt;stripped&gt;</c>) and never persisted in any
/// <c>.git/config</c>.
/// <c>NoCache</c> repositories are cloned straight into the run directory and never touch
/// the cache. Empty workspaces (the non-git materializer) are fresh scratch directories under
/// the same layout — no clone, no credential, no cache.
/// </summary>
public sealed class WorkspaceManager(
    IOptions<WorkflowDispatcherSettings> settings,
    ILogger<WorkspaceManager> logger)
{
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates the run workspace and returns its root, or null when no mounts are declared.
    /// </summary>
    public async Task<string?> PrepareAsync(
        Guid instanceId, IReadOnlyList<RepositoryDeclaration> repositories,
        IReadOnlyList<EmptyWorkspaceDeclaration>? emptyWorkspaces = null, CancellationToken ct = default)
    {
        emptyWorkspaces ??= [];
        if (repositories.Count == 0 && emptyWorkspaces.Count == 0)
            return null;

        // Every source is validated BEFORE anything touches disk: mount ids become host paths
        // and clone sources become git arguments — neither may escape what the run is owed.
        foreach (var workspace in emptyWorkspaces)
            EnsureSafeMountId(workspace.Id);
        foreach (var repository in repositories)
        {
            EnsureSafeMountId(repository.Id);
            if (CloneSourceRejection(repository.CloneUrl, settings.Value.EffectiveCloneTransports) is { } reason)
                throw new InvalidOperationException(
                    $"repository '{repository.Id}' clone source {StripUserInfo(repository.CloneUrl)} rejected: {reason}");
            if (repository.Branch is { } requestedBranch && !IsSafeBranchName(requestedBranch))
                throw new InvalidOperationException(
                    $"repository '{repository.Id}' branch '{requestedBranch}' is not a valid branch name");
        }

        var runRoot = RunRootFor(instanceId);
        foreach (var workspace in emptyWorkspaces)
            PrepareEmptyWorkspace(instanceId, runRoot, workspace);
        foreach (var repository in repositories)
        {
            var runRepoDir = Path.Combine(runRoot, "repos", repository.Id);
            Directory.CreateDirectory(runRepoDir);

            if (repository.NoCache || repository.AllowPush)
            {
                // Fresh clone per run: sensitive repos never enter the warm cache, and a
                // push-enabled clone (about to diverge from its origin) never shares one.
                await CloneAsync(repository, runRepoDir, settings.Value.EffectiveCloneTransports, ct);
            }
            else
            {
                var cacheDir = await EnsureWarmCacheAsync(repository, ct);
                CopyDirectory(cacheDir, runRepoDir);
            }

            // The commit identity is provisioned, never guessed by the agent: the binding's
            // values win, the platform identity is the default.
            await RunGitAsync(
                ["-C", runRepoDir, "config", "user.name",
                 string.IsNullOrWhiteSpace(repository.CommitName) ? "Auxilia Agent" : repository.CommitName],
                repository.CloneUrl, ct);
            await RunGitAsync(
                ["-C", runRepoDir, "config", "user.email",
                 string.IsNullOrWhiteSpace(repository.CommitEmail)
                     ? "agent@auxilia.invalid" : repository.CommitEmail],
                repository.CloneUrl, ct);
            // The clone is handed to the workload: its own git must not reach the runner host's
            // filesystem through file:// or local-path remotes (submodules included).
            await RunGitAsync(
                ["-C", runRepoDir, "config", "protocol.file.allow", "never"], repository.CloneUrl, ct);

            logger.LogInformation(
                "Workspace repository prepared. InstanceId={InstanceId} RepositoryId={RepositoryId} CloneUrl={CloneUrl} NoCache={NoCache}",
                instanceId, repository.Id, StripUserInfo(repository.CloneUrl), repository.NoCache);
        }

        return runRoot;
    }

    /// <summary>
    /// The empty-workspace materializer: a fresh scratch directory per run under the same
    /// <c>repos/&lt;id&gt;</c> layout as repository mounts. A bound working directory is
    /// pre-created so the announced mount root exists — but must stay inside the mount.
    /// </summary>
    private void PrepareEmptyWorkspace(Guid instanceId, string runRoot, EmptyWorkspaceDeclaration workspace)
    {
        var mountDir = Path.Combine(runRoot, "repos", workspace.Id);
        var targetDir = workspace.WorkingDirectory is { Length: > 0 } workingDirectory
            ? Path.Combine(mountDir, workingDirectory.Trim('/', '\\'))
            : mountDir;
        var fullMountDir = Path.GetFullPath(mountDir);
        var fullTargetDir = Path.GetFullPath(targetDir);
        if (fullTargetDir != fullMountDir
            && !fullTargetDir.StartsWith(fullMountDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"working directory '{workspace.WorkingDirectory}' escapes workspace mount '{workspace.Id}'.");
        Directory.CreateDirectory(fullTargetDir);

        logger.LogInformation(
            "Empty workspace prepared. InstanceId={InstanceId} MountId={MountId}",
            instanceId, workspace.Id);
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
    /// Clones the repository into the warm cache on first use; refreshes an existing entry via
    /// an authenticated <c>git fetch</c> and advances the checkout to the fetched tip of the
    /// requested (or default) branch, so runs start on the current commit. A failed fetch logs
    /// a warning and proceeds with the stale cache — availability over freshness; the branch
    /// checkout still happens from the refs the cache already holds.
    /// </summary>
    private async Task<string> EnsureWarmCacheAsync(RepositoryDeclaration repository, CancellationToken ct)
    {
        var cacheDir = Path.Combine(settings.Value.WarmCacheDirectory, CacheKeyFor(repository.CloneUrl));
        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(settings.Value.WarmCacheDirectory);
            await CloneAsync(repository, cacheDir, settings.Value.EffectiveCloneTransports, ct);
            return cacheDir;
        }

        try
        {
            await RunGitAsync(
                ["-C", cacheDir, "fetch", "--prune", "origin"], repository.CloneUrl, ct, authenticated: true,
                transports: settings.Value.EffectiveCloneTransports);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex,
                "Warm-cache fetch failed for repository {RepositoryId} ({CloneUrl}) — proceeding with the stale cache.",
                repository.Id, StripUserInfo(repository.CloneUrl));
        }

        var branch = repository.Branch ?? await DefaultBranchAsync(cacheDir, repository.CloneUrl, ct);
        await RunGitAsync(
            ["-C", cacheDir, "checkout", "--force", "-B", branch, $"refs/remotes/origin/{branch}"],
            repository.CloneUrl, ct);
        return cacheDir;
    }

    /// <summary>The origin's default branch as recorded at clone time (<c>origin/HEAD</c>).</summary>
    private static async Task<string> DefaultBranchAsync(string cacheDir, string cloneUrl, CancellationToken ct)
    {
        var head = await RunGitAsync(
            ["-C", cacheDir, "symbolic-ref", "--short", "refs/remotes/origin/HEAD"], cloneUrl, ct);
        const string prefix = "origin/";
        return head.StartsWith(prefix, StringComparison.Ordinal) ? head[prefix.Length..] : head;
    }

    private static async Task CloneAsync(
        RepositoryDeclaration repository, string targetDirectory, IReadOnlyList<string> transports, CancellationToken ct)
    {
        // The clone always names the credential-stripped URL: that is what git records as the
        // origin, so no .git/config — cache or per-run — ever holds the clone credential; the
        // credential rides only in the command's own -c configuration (ARCHITECTURE §8).
        var args = new List<string> { "clone" };
        if (repository.Branch is not null)
        {
            args.Add("--branch");
            args.Add(repository.Branch);
        }
        // "--" ends option parsing: a clone source can never be read as a git option
        // (e.g. --upload-pack=<command>), whatever the binding says.
        args.Add("--");
        args.Add(StripUserInfo(repository.CloneUrl));
        args.Add(targetDirectory);

        await RunGitAsync(args, repository.CloneUrl, ct, authenticated: true, transports: transports);
        // No exception for push-enabled mounts: their credential never enters the clone. The
        // dispatcher hands the push remote to the SDK alone, whose push gateway serves it.
    }

    /// <summary>
    /// Runs one git command with redirected output, a hard per-command timeout, and an empty
    /// credential helper so git never prompts; returns the trimmed stdout. An
    /// <paramref name="authenticated"/> command additionally receives the clone credential as a
    /// per-invocation URL rewrite. Non-zero exit throws with the (redacted) stderr.
    /// </summary>
    private static async Task<string> RunGitAsync(
        IReadOnlyList<string> args, string cloneUrl, CancellationToken ct, bool authenticated = false,
        IReadOnlyList<string>? transports = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("credential.helper=");
        // Transport allowlist for everything the runner fetches (the configured transports; by
        // default network ones only — no file://, local paths, ext:: (command execution) or
        // fd::) — so neither a clone source nor a submodule/redirect can make the RUNNER HOST
        // read its own filesystem or run a command.
        foreach (var protocol in TransportConfiguration(transports ?? []))
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(protocol);
        }
        if (authenticated)
            foreach (var arg in CredentialConfigFor(cloneUrl))
                psi.ArgumentList.Add(arg);
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

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {commandName} for {StripUserInfo(cloneUrl)} failed (exit {process.ExitCode}): {Redact(stderr, cloneUrl)}");
        return stdout.Trim();
    }

    private static IEnumerable<string> TransportConfiguration(IReadOnlyList<string> transports)
    {
        yield return "protocol.allow=never";
        foreach (var transport in transports)
            yield return $"protocol.{transport}.allow=always";
    }

    private static readonly Regex ScpLikeSource =
        new(@"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:(?!//)\S+$", RegexOptions.CultureInvariant);

    private static readonly Regex SafeMountId =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Why a clone source is refused, or null when its transport is one of
    /// <paramref name="transports"/> (<see cref="WorkflowDispatcherSettings.AllowedCloneTransports"/>):
    /// a URL's scheme, <c>ssh</c> for scp-like <c>user@host:path</c>, <c>file</c> for local paths.
    /// </summary>
    internal static string? CloneSourceRejection(string cloneUrl, IReadOnlyList<string> transports)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl) || cloneUrl.TrimStart().StartsWith('-'))
            return "not a repository location";
        var transport = ScpLikeSource.IsMatch(cloneUrl) ? "ssh"
            : Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.Scheme.ToLowerInvariant()
            : "file";
        return transports.Contains(transport) && transport is not ("ext" or "fd")
            ? null
            : $"transport '{transport}' is not allowed on this runner (WorkflowDispatcher:AllowedCloneTransports)";
    }

    /// <summary>A mount id becomes a host directory name: no separators, no traversal.</summary>
    internal static void EnsureSafeMountId(string mountId)
    {
        if (!SafeMountId.IsMatch(mountId) || mountId is "." or "..")
            throw new InvalidOperationException($"workspace mount id '{mountId}' is not a safe identifier.");
    }

    /// <summary>A branch name that can never be read as a git option or a revision range.</summary>
    internal static bool IsSafeBranchName(string branch)
        => branch.Length is > 0 and <= 255
           && !branch.StartsWith('-')
           && !branch.Contains("..", StringComparison.Ordinal)
           && !branch.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\');

    /// <summary>
    /// The per-command git configuration that injects a tokened URL's credential: a
    /// <c>url.&lt;tokened&gt;.insteadOf=&lt;stripped&gt;</c> rewrite, applied only to that
    /// invocation. Empty for a credential-less URL.
    /// </summary>
    internal static IReadOnlyList<string> CredentialConfigFor(string cloneUrl)
    {
        var stripped = StripUserInfo(cloneUrl);
        return string.Equals(stripped, cloneUrl, StringComparison.Ordinal)
            ? []
            : ["-c", $"url.{cloneUrl}.insteadOf={stripped}"];
    }

    /// <summary>
    /// Cache directory name: first 16 hex chars of the credential-STRIPPED clone URL's SHA-256 —
    /// a token rotation reuses the entry instead of adding a full clone.
    /// </summary>
    internal static string CacheKeyFor(string cloneUrl)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StripUserInfo(cloneUrl))))[..16]
            .ToLowerInvariant();

    /// <summary>Strips userinfo (credentials) from a clone URL for logging and for git's origin.</summary>
    internal static string StripUserInfo(string cloneUrl)
        => Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)
            // Host | Port (not HostAndPort): a default port stays implicit, so the stripped form
            // equals the plain URL the dispatcher started from — same cache key, exact rewrite.
            ? uri.GetComponents(
                UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.PathAndQuery,
                UriFormat.UriEscaped)
            : cloneUrl;

    private static string Redact(string text, string cloneUrl)
    {
        var redacted = text.Replace(cloneUrl, StripUserInfo(cloneUrl));
        return Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)
            ? redacted.Replace(uri.UserInfo, "***")
            : redacted;
    }

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

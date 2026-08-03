using System.Diagnostics;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Workflows.Workspace;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Component tests for <see cref="WorkspaceManager"/> using the real git CLI against
/// local-disk origin repositories — fast, no network.
/// </summary>
[TestFixture]
[Category("Component")]
public class WorkspaceManagerTests
{
    private string _tempRoot = null!;
    private string _originRepo = null!;
    private WorkflowDispatcherSettings _settings = null!;
    private WorkspaceManager _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"auxilia-workspace-tests-{Guid.NewGuid():N}");
        _originRepo = Path.Combine(_tempRoot, "origin");
        CreateOriginRepo(_originRepo);

        _settings = new WorkflowDispatcherSettings
        {
            WorkspaceRootDirectory = Path.Combine(_tempRoot, "workspaces"),
            WarmCacheDirectory = Path.Combine(_tempRoot, "repo-cache")
        };
        _sut = TestStores.NewWorkspaceManager(_settings);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
            DeleteDirectory(_tempRoot);
    }

    // ------------------------------------------------------------------ empty declaration

    [Test]
    public async Task Prepare_EmptyRepositoryList_ReturnsNullAndCreatesNoDirectories()
    {
        var result = await _sut.PrepareAsync(Guid.NewGuid(), [], ct: CancellationToken.None);

        Assert.That(result, Is.Null);
        Assert.That(Directory.Exists(_settings.WorkspaceRootDirectory), Is.False);
        Assert.That(Directory.Exists(_settings.WarmCacheDirectory), Is.False);
    }

    // ------------------------------------------------------------------ warm-cache path

    [Test]
    public async Task Prepare_SingleRepo_RunDirectoryContainsCommittedFile()
    {
        var instanceId = Guid.NewGuid();

        var runRoot = await _sut.PrepareAsync(
            instanceId, [new RepositoryDeclaration("main", _originRepo)], ct: CancellationToken.None);

        Assert.That(runRoot, Is.EqualTo(
            Path.Combine(_settings.WorkspaceRootDirectory, instanceId.ToString("N"))));
        Assert.That(File.Exists(Path.Combine(runRoot!, "repos", "main", "test.txt")), Is.True,
            "The committed file must be present in the per-run repo directory.");
    }

    [Test]
    public async Task Prepare_FirstUse_PopulatesWarmCache()
    {
        await _sut.PrepareAsync(
            Guid.NewGuid(), [new RepositoryDeclaration("main", _originRepo)], ct: CancellationToken.None);

        Assert.That(Directory.Exists(_settings.WarmCacheDirectory), Is.True);
        Assert.That(Directory.GetDirectories(_settings.WarmCacheDirectory), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Prepare_SecondInstance_ReusesCacheAfterOriginDeleted()
    {
        await _sut.PrepareAsync(
            Guid.NewGuid(), [new RepositoryDeclaration("main", _originRepo)], ct: CancellationToken.None);

        // The origin disappears — only the warm cache can satisfy the next run.
        // The fetch fails, is logged as a warning, and the stale cache is used.
        DeleteDirectory(_originRepo);

        var secondInstance = Guid.NewGuid();
        var runRoot = await _sut.PrepareAsync(
            secondInstance, [new RepositoryDeclaration("main", _originRepo)], ct: CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(runRoot!, "repos", "main", "test.txt")), Is.True,
            "The second run must be served from the warm cache when the origin is gone.");
    }

    // ------------------------------------------------------------------ no-cache path

    [Test]
    public async Task Prepare_NoCache_ClonesIntoRunDirectoryWithoutTouchingCache()
    {
        var runRoot = await _sut.PrepareAsync(
            Guid.NewGuid(),
            [new RepositoryDeclaration("main", _originRepo, NoCache: true)],
            ct: CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(runRoot!, "repos", "main", "test.txt")), Is.True);
        Assert.That(Directory.Exists(_settings.WarmCacheDirectory), Is.False,
            "NoCache repositories must never enter the warm cache.");
    }

    // ------------------------------------------------------------------ mixed materializers

    [Test]
    public async Task Prepare_RepositoryAndEmptyWorkspaceTogether_ShareOneRunRoot()
    {
        var runRoot = await _sut.PrepareAsync(
            Guid.NewGuid(),
            [new RepositoryDeclaration("main", _originRepo)],
            [new EmptyWorkspaceDeclaration("scratch")],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(runRoot!, "repos", "main", "test.txt")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(runRoot!, "repos", "scratch")), Is.True,
                "Both materializers land in the same per-run root.");
        });
    }

    // ------------------------------------------------------------------ identity + push

    [Test]
    public async Task Prepare_ConfiguresTheCommitIdentity_BindingValuesWinOverTheDefault()
    {
        var runRoot = await _sut.PrepareAsync(
            Guid.NewGuid(),
            [
                new RepositoryDeclaration("named", _originRepo)
                    { CommitName = "Felix Klakow", CommitEmail = "felix@example.com" },
            ],
            ct: CancellationToken.None);

        var repoDir = Path.Combine(runRoot!, "repos", "named");
        Assert.Multiple(() =>
        {
            Assert.That(GitOutput(repoDir, "config", "user.name"), Is.EqualTo("Felix Klakow"));
            Assert.That(GitOutput(repoDir, "config", "user.email"), Is.EqualTo("felix@example.com"));
        });
    }

    [Test]
    public async Task Prepare_WithoutIdentitySettings_FallsBackToThePlatformIdentity()
    {
        var runRoot = await _sut.PrepareAsync(
            Guid.NewGuid(), [new RepositoryDeclaration("main", _originRepo)], ct: CancellationToken.None);

        Assert.That(GitOutput(Path.Combine(runRoot!, "repos", "main"), "config", "user.name"),
            Is.EqualTo("Auxilia Agent"),
            "Agents must never stall on a missing commit identity.");
    }

    [Test]
    public async Task Prepare_AllowPush_ClonesFresh_AndKeepsTheOriginUrlIntact()
    {
        var runRoot = await _sut.PrepareAsync(
            Guid.NewGuid(),
            [new RepositoryDeclaration("push", _originRepo) { AllowPush = true }],
            ct: CancellationToken.None);

        var repoDir = Path.Combine(runRoot!, "repos", "push");
        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(_settings.WarmCacheDirectory), Is.False,
                "A push-enabled clone (its credential stays configured) must never enter the warm cache.");
            Assert.That(GitOutput(repoDir, "remote", "get-url", "origin"), Is.EqualTo(_originRepo),
                "The origin remote stays usable for the push.");
        });
    }

    // ------------------------------------------------------------------ failure

    [Test]
    public void Prepare_UnreachableCloneUrl_ThrowsInvalidOperationException()
    {
        var missingOrigin = Path.Combine(_tempRoot, "does-not-exist");

        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.PrepareAsync(
            Guid.NewGuid(), [new RepositoryDeclaration("main", missingOrigin)], ct: CancellationToken.None));
    }

    // ------------------------------------------------------------------ cleanup

    [Test]
    public async Task Cleanup_RemovesRunRoot()
    {
        var instanceId = Guid.NewGuid();
        var runRoot = await _sut.PrepareAsync(
            instanceId, [new RepositoryDeclaration("main", _originRepo)], ct: CancellationToken.None);
        Assert.That(Directory.Exists(runRoot), Is.True);

        await _sut.CleanupAsync(instanceId);

        Assert.That(Directory.Exists(runRoot), Is.False);
    }

    [Test]
    public void Cleanup_UnknownInstance_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _sut.CleanupAsync(Guid.NewGuid()));
    }

    // ------------------------------------------------------------------ helpers

    private static void CreateOriginRepo(string path)
    {
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "config", "user.email", "component-test@auxilia.local");
        RunGit(path, "config", "user.name", "Auxilia Component Test");
        File.WriteAllText(Path.Combine(path, "test.txt"), "hello from the workspace manager test");
        RunGit(path, "add", "test.txt");
        RunGit(path, "commit", "-m", "test: add test.txt");
    }

    /// <summary>Runs git in the given directory and returns its trimmed stdout.</summary>
    private static string GitOutput(string workingDirectory, params string[] gitArgs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in gitArgs)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout.Trim();
    }

    private static void RunGit(string workingDirectory, params string[] gitArgs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in gitArgs)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start git process.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {string.Join(' ', gitArgs)} failed (exit {process.ExitCode}): {stderr}");
        }
    }

    /// <summary>Deletes recursively, clearing the read-only attributes git sets on object files.</summary>
    private static void DeleteDirectory(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}

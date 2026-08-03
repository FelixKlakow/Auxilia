using Auxilia.Core.Runner.Workflows;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Unit tests for the empty-workspace materializer of <see cref="WorkspaceManager"/>
/// (ARCHITECTURE §9): a fresh scratch directory per run — no git, no credential, no cache.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WorkspaceManagerEmptyWorkspaceTests
{
    private string _tempRoot = null!;
    private WorkflowDispatcherSettings _settings = null!;
    private WorkspaceManager _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"auxilia-empty-workspace-tests-{Guid.NewGuid():N}");
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
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public async Task Prepare_EmptyWorkspaceOnly_CreatesAFreshScratchDirectoryUnderTheRunRoot()
    {
        var instanceId = Guid.NewGuid();

        var runRoot = await _sut.PrepareAsync(
            instanceId, [], [new EmptyWorkspaceDeclaration("scratch")], CancellationToken.None);

        Assert.That(runRoot, Is.EqualTo(
            Path.Combine(_settings.WorkspaceRootDirectory, instanceId.ToString("N"))));
        var mountDir = Path.Combine(runRoot!, "repos", "scratch");
        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(mountDir), Is.True,
                "The scratch directory must exist under the same repos/<id> layout as git mounts.");
            Assert.That(Directory.EnumerateFileSystemEntries(mountDir), Is.Empty,
                "An empty workspace materializes with no content.");
            Assert.That(Directory.Exists(_settings.WarmCacheDirectory), Is.False,
                "Empty workspaces never touch the warm cache.");
        });
    }

    [Test]
    public async Task Prepare_SeveralEmptyWorkspaces_EachGetsItsOwnDirectory()
    {
        var runRoot = await _sut.PrepareAsync(
            Guid.NewGuid(), [],
            [new EmptyWorkspaceDeclaration("a"), new EmptyWorkspaceDeclaration("b")],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(Path.Combine(runRoot!, "repos", "a")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(runRoot!, "repos", "b")), Is.True);
        });
    }

    [Test]
    public async Task Prepare_BoundWorkingDirectory_IsPreCreatedInsideTheMount()
    {
        var runRoot = await _sut.PrepareAsync(
            Guid.NewGuid(), [],
            [new EmptyWorkspaceDeclaration("scratch", "src/app")],
            CancellationToken.None);

        Assert.That(Directory.Exists(Path.Combine(runRoot!, "repos", "scratch", "src", "app")),
            Is.True, "The announced mount root must exist when the run starts.");
    }

    [Test]
    public void Prepare_WorkingDirectoryEscapingTheMount_Throws()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => _sut.PrepareAsync(
            Guid.NewGuid(), [],
            [new EmptyWorkspaceDeclaration("scratch", "../elsewhere")],
            CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("escapes workspace mount"));
    }

    [Test]
    public async Task Prepare_NothingDeclared_ReturnsNullAndCreatesNoDirectories()
    {
        var result = await _sut.PrepareAsync(Guid.NewGuid(), [], [], CancellationToken.None);

        Assert.That(result, Is.Null);
        Assert.That(Directory.Exists(_settings.WorkspaceRootDirectory), Is.False);
    }

    [Test]
    public async Task Cleanup_RemovesTheRunRoot_ExactlyLikeGitWorkspaces()
    {
        var instanceId = Guid.NewGuid();
        var runRoot = await _sut.PrepareAsync(
            instanceId, [], [new EmptyWorkspaceDeclaration("scratch")], CancellationToken.None);
        Assert.That(Directory.Exists(runRoot), Is.True);

        await _sut.CleanupAsync(instanceId);

        Assert.That(Directory.Exists(runRoot), Is.False,
            "The terminal-state cleanup is materializer-agnostic — it deletes the whole run root.");
    }
}

using Auxilia.Workflows;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class WorkspaceSetupTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"auxilia-workspace-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public void Collect_ManifestScript_ResolvesUnderTheWorkspaceReposRoot()
    {
        var tasks = WorkspaceSetup.Collect(
            [new RepositoryDeclaration("main", "https://example.test/repo.git") { SetupScript = "dotnet restore" }],
            "/workspace",
            new Dictionary<string, string>());

        Assert.That(tasks, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(tasks[0].MountId, Is.EqualTo("main"));
            Assert.That(tasks[0].Root, Is.EqualTo(Path.Combine("/workspace", "repos", "main")));
            Assert.That(tasks[0].Script, Is.EqualTo("dotnet restore"));
        });
    }

    [Test]
    public void Collect_WithoutWorkspaceDirectory_SkipsManifestScripts()
    {
        var tasks = WorkspaceSetup.Collect(
            [new RepositoryDeclaration("main", "https://example.test/repo.git") { SetupScript = "dotnet restore" }],
            workspaceDirectory: null,
            new Dictionary<string, string>());

        Assert.That(tasks, Is.Empty, "A dev/test run without a prepared workspace has nothing to set up.");
    }

    [Test]
    public void Collect_MountAnnouncedScript_UsesTheAnnouncedRoot()
    {
        var tasks = WorkspaceSetup.Collect(
            [],
            workspaceDirectory: null,
            new Dictionary<string, string>
            {
                [WorkflowEnvironmentVariables.WorkspaceMountPrefix + "MAIN"] = "/workspace/repos/main/src",
                [WorkflowEnvironmentVariables.WorkspaceMountSetupPrefix + "MAIN"] = "npm ci"
            });

        Assert.That(tasks, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(tasks[0].MountId, Is.EqualTo("MAIN"));
            Assert.That(tasks[0].Root, Is.EqualTo("/workspace/repos/main/src"));
            Assert.That(tasks[0].Script, Is.EqualTo("npm ci"));
        });
    }

    [Test]
    public void Collect_MountScriptWithoutItsRootVariable_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceSetup.Collect(
            [],
            workspaceDirectory: null,
            new Dictionary<string, string>
            {
                [WorkflowEnvironmentVariables.WorkspaceMountSetupPrefix + "MAIN"] = "npm ci"
            }));
    }

    [Test]
    public async Task RunScript_ExecutesInTheMountRoot()
    {
        var script = OperatingSystem.IsWindows() ? "echo ok> setup-ran.txt" : "echo ok > setup-ran.txt";

        await WorkspaceSetup.RunScriptAsync(
            new WorkspaceSetup.SetupTask("main", _tempRoot, script),
            NullLogger.Instance, CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_tempRoot, "setup-ran.txt")), Is.True,
            "The script must run with the mount root as its working directory.");
    }

    [Test]
    public void RunScript_NonZeroExit_FailsTheRun()
    {
        var script = OperatingSystem.IsWindows() ? "exit /b 3" : "exit 3";

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceSetup.RunScriptAsync(
            new WorkspaceSetup.SetupTask("main", _tempRoot, script),
            NullLogger.Instance, CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("'main'").And.Contain("3"));
    }

    [Test]
    public void RunScript_MissingMountDirectory_Throws()
    {
        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceSetup.RunScriptAsync(
            new WorkspaceSetup.SetupTask("main", Path.Combine(_tempRoot, "does-not-exist"), "echo hi"),
            NullLogger.Instance, CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("no prepared directory"));
    }
}

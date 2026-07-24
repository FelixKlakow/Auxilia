using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// The configuration-to-workspace convention: any slot binding whose settings carry a
/// RepositoryUrl asks for a per-run clone; a Token rides along as HTTPS userinfo only.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WorkflowDispatcherConfigurationRepositoryTests
{
    private static StoredWorkflowConfiguration ConfigurationWith(params StoredSlotBinding[] bindings)
        => new("cfg", "Cfg", "wf-type", "docker://img:test", true, bindings);

    [Test]
    public void NullConfiguration_YieldsNothing()
        => Assert.That(WorkflowDispatcher.ConfigurationRepositories(null), Is.Empty);

    [Test]
    public void BindingsWithoutRepositoryUrl_YieldNothing()
    {
        var configuration = ConfigurationWith(
            new StoredSlotBinding("coding-agent", "claude-code-cli",
                new Dictionary<string, string> { ["CliPath"] = "claude" }));

        Assert.That(WorkflowDispatcher.ConfigurationRepositories(configuration), Is.Empty);
    }

    [Test]
    public void BindingsWithoutTheMountOptIn_YieldNothing_ApiOnlyAccessStaysCloneFree()
    {
        var configuration = ConfigurationWith(
            new StoredSlotBinding("repository", "github-repository",
                new Dictionary<string, string> { ["RepositoryUrl"] = "https://github.com/acme/widget" }),
            new StoredSlotBinding("repository-2", "github-repository",
                new Dictionary<string, string>
                {
                    ["RepositoryUrl"] = "https://github.com/acme/widget",
                    ["MountIntoWorkspace"] = "false"
                }));

        Assert.That(WorkflowDispatcher.ConfigurationRepositories(configuration), Is.Empty);
    }

    [Test]
    public void RepositoryUrlWithBranch_BecomesADeclarationNamedAfterTheSlot()
    {
        var configuration = ConfigurationWith(
            new StoredSlotBinding("repository", "github-repository",
                new Dictionary<string, string>
                {
                    ["RepositoryUrl"] = "https://github.com/acme/widget",
                    ["Branch"] = "develop",
                    ["MountIntoWorkspace"] = "true"
                }));

        var declarations = WorkflowDispatcher.ConfigurationRepositories(configuration);

        Assert.That(declarations, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(declarations[0].Id, Is.EqualTo("repository"));
            Assert.That(declarations[0].CloneUrl, Is.EqualTo("https://github.com/acme/widget"));
            Assert.That(declarations[0].Branch, Is.EqualTo("develop"));
            Assert.That(declarations[0].NoCache, Is.False, "public clones may use the warm cache");
        });
    }

    [Test]
    public void Token_BecomesHttpsUserInfo_AndDisablesTheWarmCache()
    {
        var configuration = ConfigurationWith(
            new StoredSlotBinding("repository", "github-repository",
                new Dictionary<string, string>
                {
                    ["RepositoryUrl"] = "https://github.com/acme/widget",
                    ["Token"] = "ghp_secret",
                    ["MountIntoWorkspace"] = "true"
                }));

        var declarations = WorkflowDispatcher.ConfigurationRepositories(configuration);

        Assert.Multiple(() =>
        {
            Assert.That(declarations[0].CloneUrl,
                Does.StartWith("https://x-access-token:ghp_secret@github.com/acme/widget"));
            Assert.That(declarations[0].NoCache, Is.True,
                "a credentialed clone must never enter the warm cache");
        });
    }

    [Test]
    public void Token_LeavesNonHttpUrlsAndExistingUserInfoUntouched()
    {
        var configuration = ConfigurationWith(
            new StoredSlotBinding("local", "p1",
                new Dictionary<string, string>
                {
                    ["RepositoryUrl"] = @"C:\repos\local",
                    ["Token"] = "t",
                    ["MountIntoWorkspace"] = "true"
                }),
            new StoredSlotBinding("carried", "p2",
                new Dictionary<string, string>
                {
                    ["RepositoryUrl"] = "https://user:pw@example.org/repo.git",
                    ["Token"] = "t",
                    ["MountIntoWorkspace"] = "true"
                }));

        var declarations = WorkflowDispatcher.ConfigurationRepositories(configuration);

        Assert.Multiple(() =>
        {
            Assert.That(declarations[0].CloneUrl, Is.EqualTo(@"C:\repos\local"));
            Assert.That(declarations[1].CloneUrl, Is.EqualTo("https://user:pw@example.org/repo.git"));
        });
    }

    [Test]
    public void WorkspaceBind_UsesTheHostView_WhenConfigured()
    {
        var instanceId = Guid.NewGuid();
        var settings = new WorkflowDispatcherSettings
        {
            WorkspaceRootDirectory = "/workspaces",
            WorkspaceRootHostDirectory = @"C:\temp\auxilia-workspaces\"
        };

        Assert.That(WorkflowDispatcher.ResolveWorkspaceDirectoryBind(settings, instanceId),
            Is.EqualTo($@"C:\temp\auxilia-workspaces\{instanceId:N}"));
    }

    [Test]
    public void WorkspaceBind_FallsBackToTheLocalView_WithoutHostDirectory()
    {
        var instanceId = Guid.NewGuid();
        var settings = new WorkflowDispatcherSettings { WorkspaceRootDirectory = "ws-root" };

        Assert.That(WorkflowDispatcher.ResolveWorkspaceDirectoryBind(settings, instanceId),
            Is.EqualTo(Path.Combine("ws-root", instanceId.ToString("N"))));
    }
}

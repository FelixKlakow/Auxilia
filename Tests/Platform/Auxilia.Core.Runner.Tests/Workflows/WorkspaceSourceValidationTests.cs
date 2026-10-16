using Auxilia.Core.Runner.Workflows;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Clone sources, branches and mount ids reach git arguments and host paths: anything that could
/// read the runner host's filesystem, run a command, or escape the run root is refused before
/// the workspace manager touches disk.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkspaceSourceValidationTests
{
    [TestCase("https://github.com/org/repo.git")]
    [TestCase("http://git-server:8080/repo.git")]
    [TestCase("ssh://git@github.com/org/repo.git")]
    [TestCase("git://example.org/repo.git")]
    [TestCase("git@github.com:org/repo.git")]
    public void NetworkTransports_AreAccepted(string source)
        => Assert.That(WorkspaceManager.CloneSourceRejection(source, Defaults), Is.Null);

    private static readonly IReadOnlyList<string> Defaults = new WorkflowDispatcherSettings().EffectiveCloneTransports;
    private static readonly IReadOnlyList<string> WithLocal = new WorkflowDispatcherSettings
        { AllowedCloneTransports = ["https", "ssh", "file"] }.EffectiveCloneTransports;

    [TestCase("file:///etc")]
    [TestCase("/var/lib/auxilia/repo-cache/abc")]
    [TestCase(@"C:\Users\someone\repo")]
    [TestCase("../other-run")]
    public void LocalSources_AreRefused_UnlessTheRunnerOptsIn(string source)
    {
        Assert.That(WorkspaceManager.CloneSourceRejection(source, Defaults), Is.Not.Null);
        Assert.That(WorkspaceManager.CloneSourceRejection(source, WithLocal), Is.Null);
    }

    [TestCase("ext::sh -c touch% /tmp/pwned")]
    [TestCase("fd::17")]
    [TestCase("--upload-pack=touch /tmp/pwned")]
    [TestCase("-u evil")]
    [TestCase("")]
    public void CommandTransportsAndOptionInjection_AreAlwaysRefused(string source)
    {
        var everything = new WorkflowDispatcherSettings
            { AllowedCloneTransports = ["https", "file", "ext", "fd"] }.EffectiveCloneTransports;
        Assert.That(WorkspaceManager.CloneSourceRejection(source, Defaults), Is.Not.Null);
        Assert.That(WorkspaceManager.CloneSourceRejection(source, everything), Is.Not.Null,
            "command transports are never honoured, even when an operator lists them");
    }

    [Test]
    public void AnOperatorMayNarrowTheTransports()
    {
        var httpsOnly = new WorkflowDispatcherSettings { AllowedCloneTransports = ["https"] }.EffectiveCloneTransports;
        Assert.That(WorkspaceManager.CloneSourceRejection("http://git.example/repo.git", httpsOnly), Is.Not.Null);
        Assert.That(WorkspaceManager.CloneSourceRejection("https://git.example/repo.git", httpsOnly), Is.Null);
    }

    [TestCase("main", true)]
    [TestCase("feature/login-2", true)]
    [TestCase("--orphan", false)]
    [TestCase("a..b", false)]
    [TestCase("x y", false)]
    [TestCase("ref:evil", false)]
    public void BranchNames_CannotBecomeOptionsOrRanges(string branch, bool safe)
        => Assert.That(WorkspaceManager.IsSafeBranchName(branch), Is.EqualTo(safe));

    [TestCase("main")]
    [TestCase("repo_2.backend-x")]
    public void SafeMountIds_AreAccepted(string mountId)
        => Assert.DoesNotThrow(() => WorkspaceManager.EnsureSafeMountId(mountId));

    [TestCase("../../etc")]
    [TestCase("..")]
    [TestCase("a/b")]
    [TestCase(@"a\b")]
    [TestCase("")]
    [TestCase(".hidden")]
    public void TraversingMountIds_AreRefused(string mountId)
        => Assert.Throws<InvalidOperationException>(() => WorkspaceManager.EnsureSafeMountId(mountId));

    [Test]
    public void Prepare_RefusesAForbiddenSource_BeforeTouchingDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), $"auxilia-src-validation-{Guid.NewGuid():N}");
        var manager = new WorkspaceManager(
            Options.Create(new WorkflowDispatcherSettings
            {
                WorkspaceRootDirectory = Path.Combine(root, "workspaces"),
                WarmCacheDirectory = Path.Combine(root, "cache")
            }),
            NullLogger<WorkspaceManager>.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareAsync(
            Guid.NewGuid(), [new RepositoryDeclaration("main", "file:///etc")]));

        Assert.That(ex!.Message, Does.Contain("rejected"));
        Assert.That(Directory.Exists(root), Is.False, "nothing may be created for a refused source");
    }
}

using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public sealed class ProcessGitRunnerTests
{
    [Test]
    public void Combine_Success_IsStdoutOnly()
        => Assert.That(ProcessGitRunner.Combine(0, "M file.cs\n", "warning: noise\n"), Is.EqualTo("M file.cs\n"));

    [Test]
    public void Combine_Failure_AppendsStderr()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProcessGitRunner.Combine(1, "", "fatal: no upstream\n"), Is.EqualTo("fatal: no upstream\n"));
            Assert.That(ProcessGitRunner.Combine(1, "partial", "fatal: rejected"), Is.EqualTo("partial\nfatal: rejected"));
            Assert.That(ProcessGitRunner.Combine(1, "partial\n", "fatal: rejected"), Is.EqualTo("partial\nfatal: rejected"));
            Assert.That(ProcessGitRunner.Combine(1, "only stdout", "  "), Is.EqualTo("only stdout"));
        });
    }

    [Test]
    public async Task RunAsync_FailingGit_SurfacesStderrInTheOutput()
    {
        var (exitCode, output) = await new ProcessGitRunner().RunAsync(
            Path.GetTempPath(), "definitely-not-a-git-command", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("definitely-not-a-git-command"),
                "git explains the failure on stderr — a push failure must carry that text");
        });
    }

    [Test]
    public async Task RunAsync_LargeStderr_DoesNotDeadlock()
    {
        // git writes its whole usage text to stderr — well beyond a pipe buffer when repeated;
        // the runner must drain both pipes concurrently and still return.
        var runner = new ProcessGitRunner();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 3; i++)
        {
            var (exitCode, output) = await runner.RunAsync(Path.GetTempPath(), "--no-such-option-anywhere", timeout.Token);
            Assert.That(exitCode, Is.Not.EqualTo(0));
            Assert.That(output, Is.Not.Empty);
        }
    }
}

using System.Text.Json;
using Auxilia.SessionNotifier.Workflow;
using Auxilia.Workflows.TaskSource;
using Moq;

namespace Auxilia.SessionNotifier.Workflow.Tests;

[TestFixture]
[Category("Unit")]
public class NotifierApplicationTests
{
    private string _artifactPath = null!;
    private Mock<IWorkItemAccess> _workItems = null!;

    [SetUp]
    public void SetUp()
    {
        _artifactPath = Path.Combine(Path.GetTempPath(), $"session-result-{Guid.NewGuid():N}.json");
        _workItems = new Mock<IWorkItemAccess>(MockBehavior.Strict);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_artifactPath))
            File.Delete(_artifactPath);
    }

    private void WriteArtifact(SessionSummary summary)
        => File.WriteAllText(_artifactPath, JsonSerializer.Serialize(summary));

    [Test]
    public async Task Run_PostsTheFormattedSummary_OnTheOriginatingWorkItem()
    {
        WriteArtifact(new SessionSummary("cc-session/abc123", ["src/One.cs", "NOTES.md"], TimedOut: false));
        string? posted = null;
        _workItems
            .Setup(w => w.PostCommentAsync("mail-42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, comment, _) => posted = comment)
            .Returns(Task.CompletedTask);

        await new NotifierApplication(
                _workItems.Object, null, new NotifierRunContext(_artifactPath, "mail-42"))
            .RunAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(posted, Does.Contain("cc-session/abc123"));
            Assert.That(posted, Does.Contain("- src/One.cs").And.Contain("- NOTES.md"));
            Assert.That(posted, Does.Contain("Changed files (2)"));
            Assert.That(posted, Does.Not.Contain("maximum duration"));
        });
    }

    [Test]
    public void Run_WithoutWorkItem_OrWithoutArtifact_Fails()
    {
        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => new NotifierApplication(
                    _workItems.Object, null, new NotifierRunContext(_artifactPath, null))
                .RunAsync(CancellationToken.None), "no work item");
            Assert.ThrowsAsync<InvalidOperationException>(() => new NotifierApplication(
                    _workItems.Object, null, new NotifierRunContext("/nonexistent.json", "mail-42"))
                .RunAsync(CancellationToken.None), "no materialized artifact");
        });
    }

    [Test]
    public void FormatSummary_MentionsTheTimeout_AndHandlesNoChanges()
    {
        var text = NotifierApplication.FormatSummary(
            new SessionSummary("cc-session/x", [], TimedOut: true));

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("(no files changed)"));
            Assert.That(text, Does.Contain("maximum duration"));
        });
    }
}

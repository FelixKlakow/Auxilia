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
    public void Run_WithoutWorkItem_CompletesQuietly_ManualSessionsHaveNoRecipient()
    {
        Assert.DoesNotThrowAsync(() => new NotifierApplication(
                _workItems.Object, null, new NotifierRunContext(_artifactPath, null))
            .RunAsync(CancellationToken.None));
        _workItems.VerifyNoOtherCalls();
    }

    [Test]
    public void Run_WithoutMaterializedArtifact_Fails()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => new NotifierApplication(
                _workItems.Object, null, new NotifierRunContext("/nonexistent.json", "mail-42"))
            .RunAsync(CancellationToken.None));
    }

    [Test]
    public async Task Run_PostsTheFormattedAgentReport_ForSessionReportArtifacts()
    {
        File.WriteAllText(_artifactPath, JsonSerializer.Serialize(new AgentSessionReport(
            "Fix the flaky test", Success: true, "Stabilized the retry loop.",
            TurnCount: 12, TotalCostUsd: 1.5m, DurationMs: 65_000, ErrorMessage: null)));
        string? posted = null;
        _workItems
            .Setup(w => w.PostCommentAsync("mail-7", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, comment, _) => posted = comment)
            .Returns(Task.CompletedTask);

        await new NotifierApplication(
                _workItems.Object, null, new NotifierRunContext(_artifactPath, "mail-7"))
            .RunAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(posted, Does.Contain("finished"));
            Assert.That(posted, Does.Contain("Fix the flaky test"));
            Assert.That(posted, Does.Contain("Stabilized the retry loop."));
            Assert.That(posted, Does.Contain("Turns: 12").And.Contain("Duration: 00:01:05").And.Contain("Cost: $1.50"));
        });
    }

    [Test]
    public void FormatReport_FailedRun_CarriesTheErrorAndSkipsAbsentFigures()
    {
        var text = NotifierApplication.FormatReport(new AgentSessionReport(
            "Do the thing", Success: false, Summary: null,
            TurnCount: 3, TotalCostUsd: null, DurationMs: null, ErrorMessage: "CLI exited 1"));

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("failed"));
            Assert.That(text, Does.Contain("CLI exited 1"));
            Assert.That(text, Does.Contain("Turns: 3"));
            Assert.That(text, Does.Not.Contain("Duration:").And.Not.Contain("Cost:"));
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

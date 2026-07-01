using System.Text.Json;
using Auxilia.ClaudeCode.Workflow;

namespace Auxilia.ClaudeCode.Workflow.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class SessionReportWriterTests
{
    private string _outputDir = "";

    [SetUp]
    public void SetUp()
        => _outputDir = Path.Combine(Path.GetTempPath(), $"cc-report-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    [Test]
    public async Task WriteAsync_CreatesDirectoryAndRoundTrips()
    {
        var report = new SessionReport(
            "Fix it", true, "Fixed.", 3, 0.01m, 1800, null,
            new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));

        var path = await new SessionReportWriter(_outputDir).WriteAsync(report);

        Assert.That(path, Is.EqualTo(Path.Combine(_outputDir, "session-report.json")));
        var roundTripped = JsonSerializer.Deserialize<SessionReport>(await File.ReadAllTextAsync(path));
        Assert.That(roundTripped, Is.EqualTo(report));
    }
}

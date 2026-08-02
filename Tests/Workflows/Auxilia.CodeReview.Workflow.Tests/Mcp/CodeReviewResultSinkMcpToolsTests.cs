using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
using Auxilia.CodeReview.Workflow.Verdicts;

namespace Auxilia.CodeReview.Workflow.Tests.Mcp;

[TestFixture]
public sealed class CodeReviewResultSinkMcpToolsTests
{
    [Test]
    public void RecordFinding_AccumulatesTypedStagedFinding()
    {
        var sink = new CodeReviewResultSinkMcpTools("slot");

        sink.RecordFinding("file.cs", 1, 5, FindingSeverity.High, "cat", "msg", null);

        var findings = sink.DrainFindings();
        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].FilePath, Is.EqualTo("file.cs"));
        Assert.That(findings[0].LineStart, Is.EqualTo(1));
        Assert.That(findings[0].LineEnd, Is.EqualTo(5));
        Assert.That(findings[0].Severity, Is.EqualTo(FindingSeverity.High));
    }

    [Test]
    public void DrainFindings_ClearsCollection()
    {
        var sink = new CodeReviewResultSinkMcpTools("slot");

        sink.RecordFinding("a.cs", 1, 1, FindingSeverity.Low, "cat", "msg1", null);
        sink.RecordFinding("b.cs", 2, 2, FindingSeverity.Low, "cat", "msg2", null);

        var first = sink.DrainFindings();
        Assert.That(first, Has.Count.EqualTo(2));

        var second = sink.DrainFindings();
        Assert.That(second, Is.Empty);
    }

    [Test]
    public void RecordFileVerdict_IsReturnedByTakeFileVerdict()
    {
        var sink = new CodeReviewResultSinkMcpTools("slot");

        sink.RecordFileVerdict(FileVerdict.Skipped);

        Assert.That(sink.TakeFileVerdict(), Is.EqualTo(FileVerdict.Skipped));
        Assert.That(sink.TakeFileVerdict(), Is.Null);
    }

    [Test]
    public void RecordSecondaryVerdict_IsReturnedByTakeSecondaryVerdict()
    {
        var sink = new CodeReviewResultSinkMcpTools("slot");

        sink.RecordSecondaryVerdict(SecondaryVerdict.Rejected);

        Assert.That(sink.TakeSecondaryVerdict(), Is.EqualTo(SecondaryVerdict.Rejected));
        Assert.That(sink.TakeSecondaryVerdict(), Is.Null);
    }

    [Test]
    public async Task RecordFinding_ConcurrentCalls_AllAccumulated()
    {
        var sink = new CodeReviewResultSinkMcpTools("slot");

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            sink.RecordFinding($"file{i}.cs", i, i, FindingSeverity.Info, "cat", "msg", null)));

        await Task.WhenAll(tasks);

        Assert.That(sink.DrainFindings(), Has.Count.EqualTo(20));
    }

    [Test]
    public void RecordFinding_InvalidSeverityValue_AccumulatesWithoutCoercion()
    {
        var sink = new CodeReviewResultSinkMcpTools("slot");

        // The sink does not perform enum validation — that is the JSON binding layer's responsibility.
        // Calling with an out-of-range cast value should accumulate the finding as-is (no silent default coercion).
        sink.RecordFinding("file.cs", 1, 1, (FindingSeverity)999, "cat", "msg", null);

        var findings = sink.DrainFindings();
        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That((int)findings[0].Severity, Is.EqualTo(999));
    }
}

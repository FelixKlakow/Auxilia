using Auxilia.ImplementationWorkflow.Mcp;

namespace Auxilia.ImplementationWorkflow.Tests.Mcp;

[TestFixture]
public sealed class ImplementationReviewResultSinkMcpToolsTests
{
    [Test]
    public void RecordReviewNote_AccumulatesTypedNote()
    {
        var sink = new ImplementationReviewResultSinkMcpTools("slot");

        sink.RecordReviewNote("desc", "file.cs", ReviewNoteSeverity.Warning);

        var notes = sink.DrainNotes();
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0].Description, Is.EqualTo("desc"));
        Assert.That(notes[0].FilePath, Is.EqualTo("file.cs"));
        Assert.That(notes[0].Severity, Is.EqualTo(ReviewNoteSeverity.Warning));
    }

    [Test]
    public void DrainNotes_ClearsCollection()
    {
        var sink = new ImplementationReviewResultSinkMcpTools("slot");

        sink.RecordReviewNote("desc1", null, ReviewNoteSeverity.Info);
        sink.RecordReviewNote("desc2", null, ReviewNoteSeverity.Error);

        var first = sink.DrainNotes();
        Assert.That(first, Has.Count.EqualTo(2));

        var second = sink.DrainNotes();
        Assert.That(second, Is.Empty);
    }

    [Test]
    public void RecordReviewNote_NullFilePath_IsAllowed()
    {
        var sink = new ImplementationReviewResultSinkMcpTools("slot");

        sink.RecordReviewNote("desc", null, ReviewNoteSeverity.Info);

        Assert.That(sink.DrainNotes().Single().FilePath, Is.Null);
    }

    [Test]
    public async Task RecordReviewNote_ConcurrentCalls_AllAccumulated()
    {
        var sink = new ImplementationReviewResultSinkMcpTools("slot");

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            sink.RecordReviewNote($"desc{i}", null, ReviewNoteSeverity.Info)));

        await Task.WhenAll(tasks);

        Assert.That(sink.DrainNotes(), Has.Count.EqualTo(20));
    }

    [Test]
    public void RecordReviewNote_InvalidSeverityValue_AccumulatesWithoutCoercion()
    {
        var sink = new ImplementationReviewResultSinkMcpTools("slot");

        // The sink does not perform enum validation — that is the JSON binding layer's responsibility.
        // Calling with an out-of-range cast value should accumulate the note as-is (no silent default coercion).
        sink.RecordReviewNote("desc", null, (ReviewNoteSeverity)999);

        var notes = sink.DrainNotes();
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That((int)notes[0].Severity, Is.EqualTo(999));
    }
}

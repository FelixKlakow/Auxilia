using Auxilia.Core.Api.Services;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class RunStreamCursorTests
{
    [Test]
    public void ACursor_RoundTripsThroughItsText()
    {
        var cursor = new RunStreamCursor(42, RunStreamCursor.MarkOf("Running", null, DateTimeOffset.UtcNow));

        Assert.That(RunStreamCursor.TryParse(cursor.ToString(), out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(cursor));
    }

    [Test]
    public void ABareSequence_IsAPositionWithoutAStatus()
    {
        Assert.That(RunStreamCursor.TryParse("17", out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(new RunStreamCursor(17, null)), "the client then gets the current status first");
    }

    [TestCase("")]
    [TestCase("-1")]
    [TestCase("abc")]
    [TestCase("4.")]
    [TestCase("4.ABCDEF12")]
    [TestCase("4.abc")]
    [TestCase("4.abcdef1234")]
    [TestCase("4.abcdefgh")]
    [TestCase("1 2")]
    public void AnythingThisCoreNeverIssued_IsRefused(string text)
        => Assert.That(RunStreamCursor.TryParse(text, out _), Is.False);

    [Test]
    public void TheStatusMark_TellsStatusesApart_AtTheMillisecondStoresKeep()
    {
        var at = new DateTimeOffset(2026, 11, 8, 12, 0, 0, 123, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(RunStreamCursor.MarkOf("Running", null, at.AddTicks(4567)),
                Is.EqualTo(RunStreamCursor.MarkOf("Running", null, at)), "sub-millisecond precision is not kept by every store");
            Assert.That(RunStreamCursor.MarkOf("Received", null, at), Is.Not.EqualTo(RunStreamCursor.MarkOf("Running", null, at)));
            Assert.That(RunStreamCursor.MarkOf("Failed", "boom", at), Is.Not.EqualTo(RunStreamCursor.MarkOf("Failed", "bang", at)));
            Assert.That(RunStreamCursor.MarkOf("Running", null, at.AddMilliseconds(1)),
                Is.Not.EqualTo(RunStreamCursor.MarkOf("Running", null, at)));
        });
    }
}

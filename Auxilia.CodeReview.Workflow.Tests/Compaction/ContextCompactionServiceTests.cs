using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace Auxilia.CodeReview.Workflow.Tests.Compaction;

[TestFixture]
public sealed class ContextCompactionServiceTests
{
    private static ContextCompactionService Build(double limit = 1000, double fraction = 0.8) =>
        new(Options.Create(new ContextCompactionOptions
        {
            TokenLimitThreshold = limit,
            CompactionTriggerFraction = fraction
        }));

    [Test]
    public void ShouldCompact_BelowThreshold_ReturnsFalse()
    {
        var svc = Build(limit: 1000, fraction: 0.8);
        Assert.That(svc.ShouldCompact(799), Is.False);
    }

    [Test]
    public void ShouldCompact_AtThreshold_ReturnsTrue()
    {
        var svc = Build(limit: 1000, fraction: 0.8);
        Assert.That(svc.ShouldCompact(800), Is.True);
    }

    [Test]
    public void ShouldCompact_AboveThreshold_ReturnsTrue()
    {
        var svc = Build(limit: 1000, fraction: 0.8);
        Assert.That(svc.ShouldCompact(999), Is.True);
    }

    [Test]
    public async Task CompactAsync_ForwardsToSession()
    {
        var svc = Build();
        string? receivedFocus = null;
        var session = new FakeAiSession([])
        {
            CompactAsyncCallback = (focus, _) => { receivedFocus = focus; return Task.CompletedTask; }
        };

        await svc.CompactAsync(session, "focus text");

        Assert.That(receivedFocus, Is.EqualTo("focus text"));
    }
}

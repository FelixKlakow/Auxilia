using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.Workflows.AiAgent;
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
    public async Task CompactAsync_ReturnsNewSession()
    {
        var svc = Build();
        var fakeAgent = new FakeAiAgent();
        var original = await fakeAgent.OpenSessionAsync();

        var replacement = await svc.CompactAsync(original, fakeAgent);

        Assert.That(replacement, Is.Not.SameAs(original));
    }

    // ---- Fake helpers ----

    private sealed class FakeAiAgent : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IAiSession>(new FakeAiSession());
    }

    private sealed class FakeAiSession : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult("summary of findings");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

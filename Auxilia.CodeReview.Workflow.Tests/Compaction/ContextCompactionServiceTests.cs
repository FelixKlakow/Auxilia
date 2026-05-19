using Auxilia.AI;
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
        var inference = new FakeAiInference();
        var original = await inference.CreateSessionAsync("primary-reviewer");

        var replacement = await svc.CompactAsync(original, inference, "primary-reviewer");

        Assert.That(replacement, Is.Not.SameAs(original));
        original.Dispose();
        replacement.Dispose();
    }

    // ---- Fake helpers ----

    private sealed class FakeAiInference : IAiInference
    {
        public Task<IAgentSession> CreateSessionAsync(string slotName)
            => Task.FromResult<IAgentSession>(new FakeSession());
    }

    private sealed class FakeSession : IAgentSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid? LinkedWorkflowId => null;
        public string SdkName => "fake";
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
        public IObservable<AgentEvent> Events => System.Reactive.Linq.Observable.Empty<AgentEvent>();
        public IAgentRequest PrepareRequest(string prompt) => new FakeRequest(this, prompt);
        public void Dispose() { }
    }

    private sealed class FakeRequest(IAgentSession session, string prompt) : IAgentRequest
    {
        public string Prompt => prompt;
        public IAgentSession Session => session;
        public IAgentRequest WithNonDefaultModel(string modelName) => this;

        public Task<TValidatorResult> ExecuteRequestAsync<TValidatorResult>(
            IAgentResultValidator<TValidatorResult> validator, CancellationToken cancellationToken)
            => validator.ValidateAsync(this, "summary of findings");
    }
}

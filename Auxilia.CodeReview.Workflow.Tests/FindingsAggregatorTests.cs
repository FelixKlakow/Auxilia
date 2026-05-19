using Auxilia.AI;
using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.Workflows.AiAgent;

namespace Auxilia.CodeReview.Workflow.Tests;

[TestFixture]
public sealed class FindingsAggregatorTests
{
    private static StagedFinding MakeFinding(string path = "a.cs", string attribution = "primary-reviewer") =>
        new(path, 1, 2, FindingSeverity.High, "Quality", "msg", null, attribution);

    private static ReviewContext MakeContext(string prId = "PR-42") => new()
    {
        PullRequest = new PullRequestReference(prId, "main", "feature"),
        RepositoryWorkingPath = "/repo",
        Files = [],
        LinkedWorkItems = []
    };

    private static IStagedFindingsStore MakeStore(params StagedFinding[] findings)
    {
        var store = new StagedFindingsStore();
        foreach (var f in findings)
            store.Append(f);
        return store;
    }

    [Test]
    public async Task PrIdentifier_DerivedFromContextPullRequest()
    {
        var svc = new TwoEyesPassService(
            new FakeAiInference("Approved"),
            new TwoEyesConfiguration { Enabled = false });
        var agg = new FindingsAggregator(svc);

        var result = await agg.AggregateAsync(MakeStore(MakeFinding()), MakeContext("PR-99"));

        Assert.That(result.PrIdentifier, Is.EqualTo("PR-99"));
    }

    [Test]
    public async Task AggregateAsync_DisabledTwoEyes_AllFindingsSurvive()
    {
        var svc = new TwoEyesPassService(
            new FakeAiInference(),
            new TwoEyesConfiguration { Enabled = false });
        var agg = new FindingsAggregator(svc);

        var store = MakeStore(MakeFinding("a.cs"), MakeFinding("b.cs"), MakeFinding("c.cs"));
        var result = await agg.AggregateAsync(store, MakeContext());

        Assert.That(result.Findings, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task AggregateAsync_SomeRejected_OnlyApprovedAndNotReviewedSurvive()
    {
        var svc = new TwoEyesPassService(
            new FakeAiInference("Approved", "Rejected", "Approved"),
            new TwoEyesConfiguration { Enabled = true, SecondarySlotName = "secondary-reviewer" });
        var agg = new FindingsAggregator(svc);

        var store = MakeStore(MakeFinding("a.cs"), MakeFinding("b.cs"), MakeFinding("c.cs"));
        var result = await agg.AggregateAsync(store, MakeContext());

        Assert.That(result.Findings, Has.Count.EqualTo(2));
        Assert.That(result.Findings, Has.None.Matches<ReviewFinding>(f => f.TwoEyesVerdict == SecondaryVerdict.Rejected));
    }

    [Test]
    public async Task PrimaryAttribution_PopulatedFromStagedFinding()
    {
        var svc = new TwoEyesPassService(
            new FakeAiInference(),
            new TwoEyesConfiguration { Enabled = false });
        var agg = new FindingsAggregator(svc);

        var store = MakeStore(MakeFinding(attribution: "my-primary-slot"));
        var result = await agg.AggregateAsync(store, MakeContext());

        Assert.That(result.Findings[0].PrimaryReviewerAttribution, Is.EqualTo("my-primary-slot"));
    }

    [Test]
    public async Task SecondaryAttribution_PopulatedWhenTwoEyesEnabled()
    {
        var svc = new TwoEyesPassService(
            new FakeAiInference("Approved"),
            new TwoEyesConfiguration { Enabled = true, SecondarySlotName = "secondary-reviewer" });
        var agg = new FindingsAggregator(svc);

        var store = MakeStore(MakeFinding());
        var result = await agg.AggregateAsync(store, MakeContext());

        Assert.That(result.Findings[0].SecondaryReviewerAttribution, Is.EqualTo("secondary-reviewer"));
    }

    [Test]
    public async Task SecondaryAttribution_NullWhenTwoEyesDisabled()
    {
        var svc = new TwoEyesPassService(
            new FakeAiInference(),
            new TwoEyesConfiguration { Enabled = false });
        var agg = new FindingsAggregator(svc);

        var store = MakeStore(MakeFinding());
        var result = await agg.AggregateAsync(store, MakeContext());

        Assert.That(result.Findings[0].SecondaryReviewerAttribution, Is.Null);
    }

    // ---- Fake helpers ----

    private sealed class FakeAiInference(params string[] verdictSequence) : IAiInference
    {
        private int _callIndex;

        public Task<IAgentSession> CreateSessionAsync(string slotName)
        {
            var verdict = verdictSequence.Length == 0 ? "Approved"
                : verdictSequence[Math.Min(_callIndex++, verdictSequence.Length - 1)];
            return Task.FromResult<IAgentSession>(new FakeSession(verdict));
        }
    }

    private sealed class FakeSession(string verdict) : IAgentSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid? LinkedWorkflowId => null;
        public string SdkName => "fake";
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
        public IObservable<AgentEvent> Events => System.Reactive.Linq.Observable.Empty<AgentEvent>();
        public IAgentRequest PrepareRequest(string prompt) => new FakeRequest(this, prompt, verdict);
        public void Dispose() { }
    }

    private sealed class FakeRequest(IAgentSession session, string prompt, string verdict) : IAgentRequest
    {
        public string Prompt => prompt;
        public IAgentSession Session => session;
        public IAgentRequest WithNonDefaultModel(string modelName) => this;

        public Task<TValidatorResult> ExecuteRequestAsync<TValidatorResult>(
            IAgentResultValidator<TValidatorResult> validator, CancellationToken cancellationToken)
            => validator.ValidateAsync(this, $"{{\"verdict\":\"{verdict}\"}}");
    }
}

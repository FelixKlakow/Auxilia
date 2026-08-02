using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
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
            new FakeAiAgent("Approved"),
            new TwoEyesConfiguration { Enabled = false });
        var agg = new FindingsAggregator(svc);

        var result = await agg.AggregateAsync(MakeStore(MakeFinding()), MakeContext("PR-99"));

        Assert.That(result.PrIdentifier, Is.EqualTo("PR-99"));
    }

    [Test]
    public async Task AggregateAsync_DisabledTwoEyes_AllFindingsSurvive()
    {
        var svc = new TwoEyesPassService(
            new FakeAiAgent(),
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
            new FakeAiAgent("Approved", "Rejected", "Approved"),
            new TwoEyesConfiguration { Enabled = true });
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
            new FakeAiAgent(),
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
            new FakeAiAgent("Approved"),
            new TwoEyesConfiguration { Enabled = true });
        var agg = new FindingsAggregator(svc);

        var store = MakeStore(MakeFinding());
        var result = await agg.AggregateAsync(store, MakeContext());

        Assert.That(result.Findings[0].SecondaryReviewerAttribution, Is.EqualTo("secondary-reviewer"));
    }

    [Test]
    public async Task SecondaryAttribution_NullWhenTwoEyesDisabled()
    {
        var svc = new TwoEyesPassService(
            new FakeAiAgent(),
            new TwoEyesConfiguration { Enabled = false });
        var agg = new FindingsAggregator(svc);

        var store = MakeStore(MakeFinding());
        var result = await agg.AggregateAsync(store, MakeContext());

        Assert.That(result.Findings[0].SecondaryReviewerAttribution, Is.Null);
    }

    // ---- Fake helpers ----

    private sealed class FakeAiAgent(params string[] verdictSequence) : IAiAgent
    {
        private int _callIndex;

        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
        {
            var verdictStr = verdictSequence.Length == 0 ? "Approved"
                : verdictSequence[Math.Min(_callIndex++, verdictSequence.Length - 1)];
            var verdict = Enum.Parse<SecondaryVerdict>(verdictStr, ignoreCase: true);
            return Task.FromResult<IAiSession>(new FakeAiSession(verdict, options));
        }
    }

    private sealed class FakeAiSession(SecondaryVerdict verdict, AiSessionOptions? options) : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var sink = options?.CapabilityTools?.OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
            sink?.RecordSecondaryVerdict(verdict);
            return Task.FromResult("");
        }

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

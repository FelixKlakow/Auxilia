using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
using Auxilia.Workflows.AiAgent;

namespace Auxilia.CodeReview.Workflow.Tests;

[TestFixture]
public sealed class TwoEyesPassServiceTests
{
    private static StagedFinding MakeFinding(string path = "a.cs") =>
        new(path, 1, 2, FindingSeverity.High, "Quality", "msg", null, "primary-reviewer");

    private static ReviewContext MakeContext() => new()
    {
        PullRequest = new PullRequestReference("PR-1", "main", "feature"),
        RepositoryWorkingPath = "/repo",
        Files = [],
        LinkedWorkItems = []
    };

    [Test]
    public async Task Disabled_ReturnsAllFindings_AsNotReviewed()
    {
        var svc = new TwoEyesPassService(
            new FakeAiAgent(SecondaryVerdict.Approved),
            new TwoEyesConfiguration { Enabled = false });

        var staged = new[] { MakeFinding("a.cs"), MakeFinding("b.cs") };
        var result = await svc.RunAsync(staged, MakeContext());

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Has.All.Matches<ReviewFinding>(f => f.TwoEyesVerdict == SecondaryVerdict.NotReviewed));
    }

    [Test]
    public async Task Disabled_NoSessionCreated()
    {
        var fakeAgent = new FakeAiAgent(SecondaryVerdict.Approved);
        var svc = new TwoEyesPassService(
            fakeAgent,
            new TwoEyesConfiguration { Enabled = false });

        await svc.RunAsync([MakeFinding()], MakeContext());

        Assert.That(fakeAgent.SessionsCreated, Is.EqualTo(0));
    }

    [Test]
    public async Task Enabled_AllApproved_AllReturnedAsApproved()
    {
        var svc = new TwoEyesPassService(
            new FakeAiAgent(SecondaryVerdict.Approved),
            new TwoEyesConfiguration { Enabled = true });

        var staged = new[] { MakeFinding("a.cs"), MakeFinding("b.cs"), MakeFinding("c.cs") };
        var result = await svc.RunAsync(staged, MakeContext());

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result, Has.All.Matches<ReviewFinding>(f => f.TwoEyesVerdict == SecondaryVerdict.Approved));
    }

    [Test]
    public async Task Enabled_SomeRejected_OnlySurvivorsReturned()
    {
        // First two findings approved, third rejected
        var svc = new TwoEyesPassService(
            new FakeAiAgent(SecondaryVerdict.Approved, SecondaryVerdict.Approved, SecondaryVerdict.Rejected),
            new TwoEyesConfiguration { Enabled = true });

        var staged = new[] { MakeFinding("a.cs"), MakeFinding("b.cs"), MakeFinding("c.cs") };
        var result = await svc.RunAsync(staged, MakeContext());

        // TwoEyesPassService returns ALL findings with their verdict; FindingsAggregator filters
        // Verify the rejected one has Rejected verdict
        Assert.That(result.Count(f => f.TwoEyesVerdict == SecondaryVerdict.Rejected), Is.EqualTo(1));
        Assert.That(result.Count(f => f.TwoEyesVerdict == SecondaryVerdict.Approved), Is.EqualTo(2));
    }

    [Test]
    public async Task Enabled_EmptyStore_NoSessionOpened()
    {
        var fakeAgent = new FakeAiAgent(SecondaryVerdict.Approved);
        var svc = new TwoEyesPassService(
            fakeAgent,
            new TwoEyesConfiguration { Enabled = true });

        var result = await svc.RunAsync([], MakeContext());

        Assert.That(result, Is.Empty);
        Assert.That(fakeAgent.SessionsCreated, Is.EqualTo(0));
    }

    [Test]
    public async Task Enabled_NoToolCall_DefaultsToApproved()
    {
        var svc = new TwoEyesPassService(
            new NoSinkFakeAiAgent(),
            new TwoEyesConfiguration { Enabled = true });

        var staged = new[] { MakeFinding("a.cs"), MakeFinding("b.cs") };
        var result = await svc.RunAsync(staged, MakeContext());

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Has.All.Matches<ReviewFinding>(f => f.TwoEyesVerdict == SecondaryVerdict.Approved),
            "Absent tool calls should fall back to Approved (documented conservative default)");
    }

    // ---- Fake helpers ----

    private sealed class FakeAiAgent(params SecondaryVerdict[] verdictSequence) : IAiAgent
    {
        private int _callIndex;
        public int SessionsCreated { get; private set; }

        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
        {
            SessionsCreated++;
            var verdict = verdictSequence.Length == 0 ? SecondaryVerdict.Approved
                : verdictSequence[Math.Min(_callIndex++, verdictSequence.Length - 1)];
            return Task.FromResult<IAiSession>(new FakeAiSession(verdict, options));
        }
    }

    private sealed class FakeAiSession(SecondaryVerdict verdict, AiSessionOptions? options) : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var sink = options?.CapabilityTools?.OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
            sink?.RecordSecondaryVerdict(verdict);
            return Task.FromResult(string.Empty);
        }

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Simulates a model that makes no tool call (no sink recording).</summary>
    private sealed class NoSinkFakeAiAgent : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAiSession>(new NoSinkFakeAiSession());
    }

    private sealed class NoSinkFakeAiSession : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

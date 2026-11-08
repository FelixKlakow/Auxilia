using System.Threading.Channels;
using Auxilia.Steering.Codec;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.Time.Testing;

namespace Auxilia.ClaudeCode.Workflow.Tests.UnitTests;

/// <summary>
/// A multi-turn session ends by itself when nobody writes: after its idle timeout past the last
/// turn it ends exactly as if the operator had sent <c>end</c> — the agent finishes gracefully,
/// the steering view says why, and the run's final status carries the reason.
/// </summary>
[TestFixture, Category("Unit")]
public sealed class AgentSessionIdleTests
{
    private string _outputDir = "";

    [SetUp]
    public void SetUp() => _outputDir = Path.Combine(Path.GetTempPath(), $"cc-idle-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    [Test]
    public async Task AMultiTurnSession_WithATwoMinuteIdleTimeout_EndsTwoMinutesAfterItsLastTurn()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var views = new RecordingViews();
        var inputs = new QueueInputs();
        var outcome = new RunOutcome();
        var agent = new TurnLoopAgent();
        var context = AgentSessionContext.FromValues(
            "Fix the bug", null, "/workspace", _outputDir, multiTurn: "true",
            idleTimeout: "2", maxIdle: TimeSpan.FromMinutes(60));

        var run = new AgentSessionApplication(agent, views, context, time, inputs, outcome).RunAsync(CancellationToken.None);

        // Turn 1 (the instruction) ends; the operator sends a second turn, which ends too.
        await views.WaitForAsync<SteeringTurnEnded>(1);
        inputs.Push(SteeringCodec.Encode(new SteeringGuidance("now the tests")));
        await views.WaitForAsync<SteeringTurnEnded>(2);

        time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.That(run.IsCompleted, Is.False, "still within the idle timeout after the last turn");

        time.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var ended = views.Of<SteeringSessionEnded>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(agent.EndedGracefully, Is.True, "ended as if the operator had sent end — not halted");
            Assert.That(agent.Turns, Is.EqualTo(new[] { "Fix the bug", "now the tests" }));
            Assert.That(ended.Success, Is.True);
            Assert.That(ended.Reason, Is.EqualTo(SessionEndReasons.IdleTimeout), "the steering view says why");
            Assert.That(outcome.Reason, Is.EqualTo(SessionEndReasons.IdleTimeout), "and so does the run's final status");
            Assert.That(views.Of<SessionProgressEntry>().Select(p => p.Message),
                Has.Some.Contains("nobody wrote for 2 min"));
        });
    }

    [Test]
    public async Task TheOperatorsEnd_IsTheReason_WhenTheyEndTheSession()
    {
        var views = new RecordingViews();
        var inputs = new QueueInputs();
        var outcome = new RunOutcome();
        var context = AgentSessionContext.FromValues(
            "Fix the bug", null, "/workspace", _outputDir, multiTurn: "true", maxIdle: TimeSpan.FromMinutes(60));

        var run = new AgentSessionApplication(new TurnLoopAgent(), views, context, TimeProvider.System, inputs, outcome)
            .RunAsync(CancellationToken.None);
        await views.WaitForAsync<SteeringTurnEnded>(1);
        inputs.Push(SteeringCodec.Encode(new SteeringEnd()));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(outcome.Reason, Is.EqualTo(SessionEndReasons.Ended));
        Assert.That(views.Of<SteeringSessionEnded>().Single().Reason, Is.EqualTo(SessionEndReasons.Ended));
    }

    [TestCase(null, 60, 60)]
    [TestCase("2", 60, 2)]
    [TestCase("0.5", 60, 0.5)]
    [TestCase("500", 60, 60)]
    public void TheIdleTimeout_IsTheRunInput_CappedByThePlatformMaximum(string? input, double maxMinutes, double expected)
    {
        var context = AgentSessionContext.FromValues(
            null, null, "/workspace", _outputDir, multiTurn: "true",
            idleTimeout: input, maxIdle: TimeSpan.FromMinutes(maxMinutes));

        Assert.That(context.IdleTimeout, Is.EqualTo(TimeSpan.FromMinutes(expected)));
    }

    [TestCase("soon")]
    [TestCase("0")]
    [TestCase("-3")]
    public void AnIdleTimeoutThatIsNoDuration_FailsTheRun(string input)
        => Assert.Throws<InvalidOperationException>(() => AgentSessionContext.FromValues(
            null, null, "/workspace", _outputDir, multiTurn: "true", idleTimeout: input));

    /// <summary>
    /// A multi-turn agent the way the providers implement one: each turn ends with the
    /// turn-ended callback, the next guidance is the next turn, and the end token ends it.
    /// </summary>
    private sealed class TurnLoopAgent : ICodingAgent
    {
        public List<string> Turns { get; } = [];
        public bool EndedGracefully { get; private set; }

        public async Task<CodingAgentResult> RunAsync(
            CodingAgentRequest request, Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
            CancellationToken cancellationToken = default)
        {
            var prompt = request.Instruction;
            while (true)
            {
                Turns.Add(prompt);
                await onChatEntry(new AgentChatEntry(AgentChatRole.Assistant, $"done: {prompt}", DateTimeOffset.UtcNow),
                    cancellationToken);
                await request.OnTurnEnded!(Turns.Count, $"done: {prompt}", cancellationToken);
                using var either = CancellationTokenSource.CreateLinkedTokenSource(request.EndToken, cancellationToken);
                try
                {
                    prompt = await request.Interaction!.WaitForGuidanceAsync(either.Token);
                }
                catch (OperationCanceledException) when (request.EndToken.IsCancellationRequested)
                {
                    EndedGracefully = true;
                    return new CodingAgentResult(true, "The session ended.", Turns.Count);
                }
            }
        }
    }

    private sealed class QueueInputs : IWorkflowInputs
    {
        private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();

        public void Push(string payloadJson) => _channel.Writer.TryWrite(payloadJson);

        public async Task<string> ReceiveAsync(CancellationToken ct = default) => await _channel.Reader.ReadAsync(ct);
    }

    private sealed class RecordingViews : IViewPublisher
    {
        private readonly List<object> _items = [];
        private readonly Lock _gate = new();

        public Task PublishAsync<TItem>(string viewName, TItem item, CancellationToken ct = default)
        {
            lock (_gate)
                _items.Add(item!);
            return Task.CompletedTask;
        }

        public List<T> Of<T>()
        {
            lock (_gate)
                return _items.OfType<T>().ToList();
        }

        public async Task WaitForAsync<T>(int count)
        {
            for (var i = 0; i < 250 && Of<T>().Count < count; i++)
                await Task.Delay(20);
            Assert.That(Of<T>(), Has.Count.GreaterThanOrEqualTo(count), $"{count} × {typeof(T).Name}");
        }
    }
}

using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Steering.Codec;
using Auxilia.Workflows.Testing.DummyWorkflows;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// Multi-turn sessions that end by themselves, end to end (real SDK session engine, runner,
/// bus and Core; the agent is scripted): after their idle timeout past the last turn — the
/// run's own <c>idle-timeout</c>, or the runner's maximum when it sets none — they end as if the
/// operator had sent <c>end</c>, say so on the steering view, and the run's final status
/// carries the reason. No run holds its container forever.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class MultiTurnSessionSystemTests
{
    [Test]
    [CancelAfter(180_000)]
    public async Task AMultiTurnRun_EndsByItself_ItsIdleTimeoutAfterItsLastTurn(CancellationToken ct)
    {
        var idle = TimeSpan.FromSeconds(6);
        var session = await RunSessionAsync(("idle-timeout", idle.TotalMinutes.ToString(
            System.Globalization.CultureInfo.InvariantCulture)), ct);

        Assert.Multiple(() =>
        {
            Assert.That(session.TurnsEnded, Is.EqualTo(2), "the instruction, then the operator's second turn");
            Assert.That(session.Verdict.State, Is.EqualTo(RunStates.Success), "ended as if end had been sent — not halted");
            Assert.That(session.Verdict.Reason, Is.EqualTo(SessionEndReasons.IdleTimeout), "the final status says why");
            Assert.That(session.SessionEnded?.Reason, Is.EqualTo(SessionEndReasons.IdleTimeout), "so does the steering view");
            Assert.That(session.IdleFor, Is.GreaterThanOrEqualTo(idle - TimeSpan.FromSeconds(1)),
                "not before the idle timeout after the last turn");
            Assert.That(session.IdleFor, Is.LessThan(idle + TimeSpan.FromSeconds(30)), "and not much after it");
            Assert.That(session.StoredReason, Is.EqualTo(SessionEndReasons.IdleTimeout));
        });
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task AMultiTurnRun_WithoutAnIdleTimeout_EndsAtThePlatformsMaximum(CancellationToken ct)
    {
        var session = await RunSessionAsync(null, ct);

        Assert.Multiple(() =>
        {
            Assert.That(session.Verdict.Reason, Is.EqualTo(SessionEndReasons.IdleTimeout));
            Assert.That(session.IdleFor, Is.GreaterThanOrEqualTo(CoreClientEnvironment.MaxIdle - TimeSpan.FromSeconds(1)),
                "the runner's cap is the timeout of a run that sets none");
            Assert.That(session.IdleFor, Is.LessThan(CoreClientEnvironment.MaxIdle + TimeSpan.FromSeconds(30)));
        });
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task AnIdleTimeoutAboveThePlatformsMaximum_IsCappedByIt(CancellationToken ct)
    {
        var session = await RunSessionAsync(("idle-timeout", "600"), ct);

        Assert.That(session.IdleFor, Is.LessThan(CoreClientEnvironment.MaxIdle + TimeSpan.FromSeconds(30)),
            "ten hours asked, the runner's maximum applies");
        Assert.That(session.Verdict.Reason, Is.EqualTo(SessionEndReasons.IdleTimeout));
    }

    private sealed record Session(
        int TurnsEnded, RunStreamStatus Verdict, SteeringSessionEnded? SessionEnded, TimeSpan IdleFor, string? StoredReason);

    /// <summary>Runs a multi-turn session, sends a second turn after the first, and watches it end on its own.</summary>
    private static async Task<Session> RunSessionAsync((string Key, string Value)? idleTimeout, CancellationToken ct)
    {
        var client = CoreClientEnvironment.CreateClient();
        var context = CoreClientEnvironment.ContextFor(IdleSessionWorkflow.WorkflowName,
            ("Title", "first turn"), ("multi-turn", "true"));
        if (idleTimeout is { } input)
            context[input.Key] = input.Value;
        var accepted = await client.RunAsync(new RunRequest(CoreClientEnvironment.IdleSessionWorkflowType, context), ct);

        var turnsEnded = 0;
        var lastTurnEnded = DateTimeOffset.MinValue;
        SteeringSessionEnded? sessionEnded = null;
        RunStreamStatus? verdict = null;
        var verdictAt = DateTimeOffset.MinValue;
        await foreach (var frame in client.StreamRunAsync(accepted.RunId, ct))
        {
            if (frame is not StreamEventFrame<RunStreamEvent> { Event: var evt })
                continue;
            if (evt.AsView() is { ViewName: "steering" } steering)
            {
                switch (SteeringCodec.Decode(steering.PayloadJson))
                {
                    case SteeringTurnEnded:
                        turnsEnded++;
                        lastTurnEnded = DateTimeOffset.UtcNow;
                        if (turnsEnded == 1)
                            await client.ProvideInputAsync(accepted.RunId,
                                SteeringCodec.Encode(new SteeringGuidance("second turn")), ct);
                        break;
                    case SteeringSessionEnded ended:
                        sessionEnded = ended;
                        break;
                }
            }
            if (evt.AsStatus() is { IsTerminal: true } status)
            {
                verdict = status;
                verdictAt = DateTimeOffset.UtcNow;
            }
        }

        Assert.That(verdict, Is.Not.Null, "the stream ends with the run's verdict");
        var stored = await client.GetRunAsync(accepted.RunId, ct);
        return new Session(turnsEnded, verdict!, sessionEnded, verdictAt - lastTurnEnded, stored?.Reason);
    }
}

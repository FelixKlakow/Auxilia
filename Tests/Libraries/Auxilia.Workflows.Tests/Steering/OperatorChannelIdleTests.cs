using Auxilia.Steering.Codec;
using Auxilia.Workflows.Steering;
using Microsoft.Extensions.Time.Testing;

namespace Auxilia.Workflows.Tests.Steering;

/// <summary>
/// A turn-based session nobody steers any more ends by itself: once it has waited its idle
/// timeout for the operator — after a turn, from a start without an instruction, or on an open
/// question — exactly as if the operator had sent <c>end</c>. Every operator input restarts the
/// clock; a running turn is never idle.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class OperatorChannelIdleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private FakeTimeProvider _time = null!;
    private OperatorChannelTests.FakeInputs _inputs = null!;
    private OperatorChannelTests.RecordingViewPublisher _views = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        _inputs = new OperatorChannelTests.FakeInputs();
        _views = new OperatorChannelTests.RecordingViewPublisher();
    }

    private Task<OperatorChannel> StartAsync(bool awaitingFirstTurn = false)
        => OperatorChannel.StartAsync(_views, _inputs, extraCapabilities: ["end"],
            idle: new SessionIdlePolicy(Timeout, awaitingFirstTurn, _time));

    [Test]
    public async Task NoTurnForTheIdleTimeout_AfterATurn_EndsTheSession_AsIfEndHadBeenSent()
    {
        await using var channel = await StartAsync();
        await channel.PublishTurnEndedAsync(1, "done", CancellationToken.None);

        _time.Advance(Timeout - Tick);
        Assert.That(channel.EndToken.IsCancellationRequested, Is.False, "still within the timeout");

        _time.Advance(Tick);
        Assert.Multiple(() =>
        {
            Assert.That(channel.EndToken.IsCancellationRequested, Is.True, "the same token the operator's end cancels");
            Assert.That(channel.HaltToken.IsCancellationRequested, Is.False, "a graceful end, not a halt");
            Assert.That(channel.EndReason, Is.EqualTo(SessionEndReasons.IdleTimeout));
        });
    }

    [Test]
    public async Task ARunningTurn_IsNeverIdle()
    {
        await using var channel = await StartAsync();

        _time.Advance(TimeSpan.FromHours(3));

        Assert.That(channel.EndToken.IsCancellationRequested, Is.False, "the agent is at work, nobody is expected to write");
    }

    [Test]
    public async Task ASessionWithoutAFirstInstruction_IsIdleFromItsStart()
    {
        await using var channel = await StartAsync(awaitingFirstTurn: true);

        _time.Advance(Timeout);

        Assert.That(channel.EndReason, Is.EqualTo(SessionEndReasons.IdleTimeout));
    }

    [Test]
    public async Task AnyOperatorInput_RestartsTheClock()
    {
        await using var channel = await StartAsync();
        await channel.PublishTurnEndedAsync(1, null, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(90));

        _inputs.Push("""{"$type":"setting","key":"model","value":"fast"}""");
        await channel.WaitForSettingAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        _time.Advance(Timeout - Tick);
        Assert.That(channel.EndToken.IsCancellationRequested, Is.False, "the operator is there");

        _time.Advance(Tick);
        Assert.That(channel.EndReason, Is.EqualTo(SessionEndReasons.IdleTimeout));
    }

    [Test]
    public async Task TheNextInstruction_StartsATurn_AndStopsTheClock()
    {
        await using var channel = await StartAsync();
        await channel.PublishTurnEndedAsync(1, null, CancellationToken.None);

        _inputs.Push("""{"$type":"guidance","text":"now the tests"}""");
        Assert.That(await channel.WaitForGuidanceAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            Is.EqualTo("now the tests"));
        _time.Advance(TimeSpan.FromHours(1));

        Assert.That(channel.EndToken.IsCancellationRequested, Is.False, "a turn is running again");
    }

    [Test]
    public async Task ALateAgentEvent_AfterATurn_OnlyRestartsTheClock()
    {
        await using var channel = await StartAsync();
        await channel.PublishTurnEndedAsync(1, null, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));

        channel.NoteAgentActivity(); // an event of the finished turn, delivered late
        _time.Advance(Timeout - Tick);
        Assert.That(channel.EndToken.IsCancellationRequested, Is.False);

        _time.Advance(Tick);
        Assert.That(channel.EndReason, Is.EqualTo(SessionEndReasons.IdleTimeout), "still waiting for the operator");
    }

    [Test]
    public async Task GuidanceSentMidTurn_IsTheNextTurn_TheAgentAtWorkOnItIsNotIdle()
    {
        await using var channel = await StartAsync();
        _inputs.Push("""{"$type":"guidance","text":"and then the docs"}""");
        await channel.WaitForGuidanceAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await channel.PublishTurnEndedAsync(1, null, CancellationToken.None);
        channel.NoteAgentActivity(); // the queued instruction's turn begins
        _time.Advance(TimeSpan.FromHours(1));

        Assert.That(channel.EndToken.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task GuidanceSentMidTurn_ThatTheAgentAbsorbed_StillEndsAnIdleSession()
    {
        await using var channel = await StartAsync();
        _inputs.Push("""{"$type":"guidance","text":"and then the docs"}""");
        await channel.WaitForGuidanceAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await channel.PublishTurnEndedAsync(1, null, CancellationToken.None);
        _time.Advance(Timeout); // no next turn ever began

        Assert.That(channel.EndReason, Is.EqualTo(SessionEndReasons.IdleTimeout));
    }

    [Test]
    public async Task AnOpenQuestion_NobodyAnswers_IsDeclined_AndTheSessionEndsWhenTheTurnFinishes()
    {
        await using var channel = await StartAsync();
        var asking = channel.AskAsync([new OperatorQuestion("q1", "Push?", [new OperatorOption("allow", "Allow")])],
            CancellationToken.None);
        _views.WaitForSingle("form-requested");

        _time.Advance(Timeout);

        var answers = await asking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(answers, Is.Empty, "no answer — the agent reads it as a decline and finishes its turn");
            Assert.That(channel.EndReason, Is.EqualTo(SessionEndReasons.IdleTimeout));
            Assert.That(channel.EndToken.IsCancellationRequested, Is.False, "the running turn finishes first");
        });

        var again = await channel.AskAsync([new OperatorQuestion("q2", "Push now?", [])], CancellationToken.None);
        Assert.That(again, Is.Empty, "an ending session asks nobody");

        await channel.PublishTurnEndedAsync(1, null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(channel.EndToken.IsCancellationRequested, Is.True, "the turn is over — now the session ends");
            Assert.That(_views.CountOf("turn-ended"), Is.Zero, "the last turn of an ending session is not announced");
            Assert.That(_views.CountOf("form-requested"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TheOperatorsEnd_WithAQuestionOpen_DeclinesIt_AndEndsAtTheTurnsEnd()
    {
        await using var channel = await StartAsync();
        var asking = channel.AskAsync([new OperatorQuestion("q1", "Push?", [])], CancellationToken.None);
        _views.WaitForSingle("form-requested");

        _inputs.Push("""{"$type":"end"}""");

        Assert.That(await asking.WaitAsync(TimeSpan.FromSeconds(5)), Is.Empty);
        Assert.That(channel.EndReason, Is.EqualTo(SessionEndReasons.Ended));
        await channel.PublishTurnEndedAsync(1, null, CancellationToken.None);
        Assert.That(channel.EndToken.IsCancellationRequested, Is.True);
    }

    [Test]
    public async Task AnEndedSession_SaysWhyItEnded()
    {
        await using var channel = await StartAsync(awaitingFirstTurn: true);
        _time.Advance(Timeout);

        await channel.EndSessionAsync(success: true, error: null, CancellationToken.None);

        Assert.That(_views.Single("session-ended").GetProperty("reason").GetString(),
            Is.EqualTo(SessionEndReasons.IdleTimeout));
    }

    [Test]
    public async Task WithoutAPolicy_TheSessionWaitsForever()
    {
        await using var channel = await OperatorChannel.StartAsync(_views, _inputs, extraCapabilities: ["end"]);
        await channel.PublishTurnEndedAsync(1, null, CancellationToken.None);

        _time.Advance(TimeSpan.FromDays(1));

        Assert.That(channel.EndToken.IsCancellationRequested, Is.False);
    }
}

using Auxilia.Workflows.AiAgent;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
[Category("Unit")]
public class ResilientAiAgentTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fails for the first <paramref name="failCount"/> calls, then returns a session.
    /// If <paramref name="alwaysThrow"/> is set, every call throws that exception.
    /// </summary>
    private sealed class FakeAiAgent : IAiAgent
    {
        private readonly int _failCount;
        private readonly Exception? _alwaysThrow;
        private int _callCount;

        public FakeAiAgent(int failCount = 0, Exception? alwaysThrow = null)
        {
            _failCount = failCount;
            _alwaysThrow = alwaysThrow;
        }

        public int CallCount => _callCount;

        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_alwaysThrow != null)
                throw _alwaysThrow;
            if (_callCount <= _failCount)
                throw new InvalidOperationException($"Simulated open-session failure #{_callCount}");
            return Task.FromResult<IAiSession>(new FakeSession("ok"));
        }
    }

    private sealed class FakeSession : IAiSession
    {
        private readonly string _response;
        public FakeSession(string response) => _response = response;
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(_response);
        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Returns a no-op delay that appends the requested duration to <paramref name="recorded"/>.</summary>
    private static Func<TimeSpan, CancellationToken, Task> RecordingDelay(List<TimeSpan> recorded)
        => (ts, _) => { recorded.Add(ts); return Task.CompletedTask; };

    private static Func<TimeSpan, CancellationToken, Task> NoDelay()
        => RecordingDelay(new List<TimeSpan>());

    // ── OpenSession: success path ─────────────────────────────────────────────

    [Test]
    public async Task OpenSession_SucceedsOnFirstAttempt_ReturnsSession()
    {
        var inner = new FakeAiAgent(failCount: 0);
        var sut = new ResilientAiAgent(inner, new AiResilienceOptions(), NoDelay());

        var session = await sut.OpenSessionAsync();

        Assert.That(session, Is.Not.Null);
    }

    [Test]
    public async Task OpenSession_SucceedsOnFirstAttempt_InnerCalledExactlyOnce()
    {
        var delays = new List<TimeSpan>();
        var inner = new FakeAiAgent(failCount: 0);
        var sut = new ResilientAiAgent(inner, new AiResilienceOptions(), RecordingDelay(delays));

        await sut.OpenSessionAsync();

        Assert.That(inner.CallCount, Is.EqualTo(1));
        Assert.That(delays, Is.Empty, "No delay should occur when the first attempt succeeds.");
    }

    // ── OpenSession: retry path ───────────────────────────────────────────────

    [Test]
    public async Task OpenSession_FailsOnceThenSucceeds_RetriesAndReturnsSession()
    {
        var inner = new FakeAiAgent(failCount: 1);
        var sut = new ResilientAiAgent(inner, new AiResilienceOptions { MaxAttempts = 3, InitialBackoffMs = 100 }, NoDelay());

        var session = await sut.OpenSessionAsync();

        Assert.That(inner.CallCount, Is.EqualTo(2));
        Assert.That(session, Is.Not.Null);
    }

    [Test]
    public async Task OpenSession_MultipleFail_BackoffDoublesBetweenAttempts()
    {
        var delays = new List<TimeSpan>();
        var inner = new FakeAiAgent(failCount: 2);
        var sut = new ResilientAiAgent(inner, new AiResilienceOptions { MaxAttempts = 3, InitialBackoffMs = 100 }, RecordingDelay(delays));

        await sut.OpenSessionAsync();

        Assert.That(delays, Has.Count.EqualTo(2), "Two delays expected: after attempt 0 and attempt 1.");
        Assert.That(delays[0].TotalMilliseconds, Is.EqualTo(100), "First delay should equal InitialBackoffMs.");
        Assert.That(delays[1].TotalMilliseconds, Is.EqualTo(200), "Second delay should be double the first.");
    }

    // ── OpenSession: exhausted attempts ──────────────────────────────────────

    [Test]
    public void OpenSession_ExceedsMaxAttempts_ThrowsLastException()
    {
        var lastEx = new InvalidOperationException("Persistent failure");
        var inner = new FakeAiAgent(alwaysThrow: lastEx);
        var sut = new ResilientAiAgent(inner, new AiResilienceOptions { MaxAttempts = 3 }, NoDelay());

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => sut.OpenSessionAsync());

        Assert.That(thrown, Is.SameAs(lastEx));
    }

    [Test]
    public void OpenSession_ExceedsMaxAttempts_InnerCalledMaxAttemptsTimes()
    {
        var inner = new FakeAiAgent(failCount: int.MaxValue);
        var sut = new ResilientAiAgent(inner, new AiResilienceOptions { MaxAttempts = 3 }, NoDelay());

        Assert.ThrowsAsync<InvalidOperationException>(() => sut.OpenSessionAsync());

        Assert.That(inner.CallCount, Is.EqualTo(3));
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Test]
    public void OpenSession_TokenCancelledBeforeFirstAttempt_ThrowsOperationCancelledException()
    {
        var inner = new FakeAiAgent(failCount: 0);
        var sut = new ResilientAiAgent(inner, new AiResilienceOptions { MaxAttempts = 3 }, NoDelay());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => sut.OpenSessionAsync(cancellationToken: cts.Token));
    }

    [Test]
    public void OpenSession_TokenCancelledBeforeFirstAttempt_InnerNeverCalled()
    {
        var inner = new FakeAiAgent(failCount: 0);
        var sut = new ResilientAiAgent(inner, new AiResilienceOptions { MaxAttempts = 3 }, NoDelay());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => sut.OpenSessionAsync(cancellationToken: cts.Token));

        Assert.That(inner.CallCount, Is.EqualTo(0));
    }

    [Test]
    public void OpenSession_TokenCancelledDuringDelay_StopsRetrying()
    {
        using var cts = new CancellationTokenSource();
        var inner = new FakeAiAgent(failCount: int.MaxValue);

        // Delay function cancels the CTS and returns a faulted task with OperationCanceledException
        // (not Task.FromCanceled, which would yield TaskCanceledException on await)
        Func<TimeSpan, CancellationToken, Task> cancellingDelay = (_, ct) =>
        {
            cts.Cancel();
            return Task.FromException(new OperationCanceledException(ct));
        };

        var sut = new ResilientAiAgent(inner, new AiResilienceOptions { MaxAttempts = 3 }, cancellingDelay);

        Assert.ThrowsAsync<OperationCanceledException>(() => sut.OpenSessionAsync(cancellationToken: cts.Token));
        Assert.That(inner.CallCount, Is.EqualTo(1), "Only the first attempt should have been made.");
    }
}

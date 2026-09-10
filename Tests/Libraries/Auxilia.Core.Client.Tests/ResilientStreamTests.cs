using System.Net;
using System.Text.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Client.Tests;

/// <summary>
/// Unit tests for the client-internal stream resilience: reconnect with backoff, terminal-close
/// detection, sequence dedupe across resubscribes, idle-timeout drop detection, non-transient
/// error policy, and the unary-timeout split. All timing is scripted — backoff 0 keeps them fast.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class ResilientStreamTests
{
    private static CoreClient NewClient(SseScriptHandler handler, CoreClientOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://core.test/") };
        return new CoreClient(http, options ?? new CoreClientOptions
        {
            StreamReconnectInitialBackoffSeconds = 0,
            StreamIdleTimeoutSeconds = 0
        });
    }

    private static string StatusLine(string state, long sequence = 0)
        => "data: " + JsonSerializer.Serialize(new RunStreamEvent(
            RunStreamEvent.StatusKind, Guid.Empty, sequence,
            JsonSerializer.Serialize(new { state }, JsonSerializerOptions.Web),
            DateTimeOffset.UtcNow), JsonSerializerOptions.Web);

    private static string ViewLine(long sequence)
        => "data: " + JsonSerializer.Serialize(new RunStreamEvent(
            RunStreamEvent.ViewKind, Guid.Empty, sequence, "{}", DateTimeOffset.UtcNow),
            JsonSerializerOptions.Web);

    private static string ViewLine(string viewName, long sequence)
        => "data: " + JsonSerializer.Serialize(new RunStreamEvent(
            RunStreamEvent.ViewKind, Guid.Empty, sequence,
            JsonSerializer.Serialize(
                new RunStreamView(Guid.Empty, viewName, sequence, "{}"), JsonSerializerOptions.Web),
            DateTimeOffset.UtcNow), JsonSerializerOptions.Web);

    private static async Task<List<ClientStreamFrame<RunStreamEvent>>> DrainAsync(
        CoreClient client, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var frames = new List<ClientStreamFrame<RunStreamEvent>>();
        await foreach (var frame in client.StreamRunAsync(Guid.NewGuid(), cts.Token))
            frames.Add(frame);
        return frames;
    }

    [Test]
    public async Task MidStreamDrop_YieldsReconnectingFrame_AndResubscribes()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK, [ViewLine(1)], SseConnectionEnd.ThrowMidStream))
            .Enqueue(new SseConnection(HttpStatusCode.OK, [ViewLine(2), StatusLine("Success")]));

        var frames = await DrainAsync(NewClient(handler));

        Assert.Multiple(() =>
        {
            Assert.That(handler.ConnectionsServed, Is.EqualTo(2));
            Assert.That(frames.OfType<StreamConnectionFrame<RunStreamEvent>>()
                    .Select(f => (f.State, f.Attempt)),
                Is.EqualTo(new[]
                {
                    (StreamConnectionState.Connected, 1),
                    (StreamConnectionState.Reconnecting, 1),
                    (StreamConnectionState.Connected, 2)
                }));
            Assert.That(frames.OfType<StreamConnectionFrame<RunStreamEvent>>()
                    .Single(f => f.State == StreamConnectionState.Reconnecting).Cause,
                Is.InstanceOf<IOException>(), "the wrapped cause rides the frame — it never throws");
            Assert.That(frames.OfType<StreamEventFrame<RunStreamEvent>>().Count(), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task OrderlyClose_WithoutTerminal_IsADrop_AndReconnects()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK, [ViewLine(1)]))
            .Enqueue(new SseConnection(HttpStatusCode.OK, [StatusLine("Success")]));

        var frames = await DrainAsync(NewClient(handler));

        Assert.That(handler.ConnectionsServed, Is.EqualTo(2),
            "an orderly close without a terminal event means the Core recycled — reconnect");
        Assert.That(frames.OfType<StreamEventFrame<RunStreamEvent>>().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task TerminalStatus_EndsTheEnumeration_NoReconnect()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK, [StatusLine("Failed")]));

        var frames = await DrainAsync(NewClient(handler));

        Assert.Multiple(() =>
        {
            Assert.That(handler.ConnectionsServed, Is.EqualTo(1),
                "a terminal status ends the stream for good — no reconnect attempt");
            Assert.That(frames.Last(), Is.InstanceOf<StreamEventFrame<RunStreamEvent>>());
        });
    }

    [Test]
    public async Task ResubscribeReplay_IsDeduplicatedBySequence_StatusFramesAlwaysPass()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK,
                [StatusLine("Running"), ViewLine(1), ViewLine(2), ViewLine(3)],
                SseConnectionEnd.ThrowMidStream))
            // The server snapshot re-delivers the status and the backfill overlap re-delivers
            // views 1-3; only the new view 4 may reach the consumer again.
            .Enqueue(new SseConnection(HttpStatusCode.OK,
                [StatusLine("Running"), ViewLine(1), ViewLine(2), ViewLine(3), ViewLine(4), StatusLine("Success")]));

        var frames = await DrainAsync(NewClient(handler));
        var events = frames.OfType<StreamEventFrame<RunStreamEvent>>().Select(f => f.Event).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(events.Where(e => e.Kind == RunStreamEvent.ViewKind).Select(e => e.Sequence),
                Is.EqualTo(new long[] { 1, 2, 3, 4 }), "view frames dedupe across the resubscribe");
            Assert.That(events.Count(e => e.Kind == RunStreamEvent.StatusKind), Is.EqualTo(3),
                "status frames are idempotent and always pass (snapshot re-delivery included)");
        });
    }

    [Test]
    public async Task InterleavedViews_WithIndependentSequences_AllFramesDelivered()
    {
        // Sequence is per-(run, view) monotonic: on a healthy first connection a view whose
        // counter lags another's must NOT be deduped against the other view's high-water mark.
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK,
                [
                    StatusLine("Running"),
                    ViewLine("workspace", 1), ViewLine("session", 1), ViewLine("workspace", 2),
                    ViewLine("session", 2), ViewLine("workspace", 3),
                    StatusLine("Success")
                ]));

        var frames = await DrainAsync(NewClient(handler));
        var events = frames.OfType<StreamEventFrame<RunStreamEvent>>().Select(f => f.Event).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(handler.ConnectionsServed, Is.EqualTo(1));
            Assert.That(events
                    .Where(e => e.Kind == RunStreamEvent.ViewKind)
                    .Select(e => (e.AsView()!.ViewName, e.Sequence)),
                Is.EqualTo(new[]
                {
                    ("workspace", 1L), ("session", 1L), ("workspace", 2L),
                    ("session", 2L), ("workspace", 3L)
                }), "every view frame of every view arrives — the dedupe is keyed per view");
            Assert.That(events.Count(e => e.Kind == RunStreamEvent.StatusKind), Is.EqualTo(2),
                "non-view frames are untouched by the dedupe");
        });
    }

    [Test]
    public async Task ResubscribeReplay_IsDeduplicatedPerView()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK,
                [ViewLine("workspace", 1), ViewLine("workspace", 2), ViewLine("session", 1)],
                SseConnectionEnd.ThrowMidStream))
            // The backfill overlap re-delivers everything; only the genuinely new frames
            // (workspace 3, session 2) may reach the consumer again.
            .Enqueue(new SseConnection(HttpStatusCode.OK,
                [
                    ViewLine("workspace", 1), ViewLine("workspace", 2), ViewLine("session", 1),
                    ViewLine("workspace", 3), ViewLine("session", 2),
                    StatusLine("Success")
                ]));

        var frames = await DrainAsync(NewClient(handler));
        var views = frames.OfType<StreamEventFrame<RunStreamEvent>>()
            .Select(f => f.Event)
            .Where(e => e.Kind == RunStreamEvent.ViewKind)
            .Select(e => (e.AsView()!.ViewName, e.Sequence))
            .ToList();

        Assert.That(views, Is.EqualTo(new[]
        {
            ("workspace", 1L), ("workspace", 2L), ("session", 1L),
            ("workspace", 3L), ("session", 2L)
        }), "duplicates dedupe within their view; the other view's counter never interferes");
    }

    [Test]
    public void NonTransientInitialError_Throws_WithoutRetry()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.Unauthorized));

        var ex = Assert.ThrowsAsync<CoreApiException>(async () => await DrainAsync(NewClient(handler)));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(handler.ConnectionsServed, Is.EqualTo(1), "auth errors are not retried");
        });
    }

    [Test]
    public async Task TransientInitialError_IsRetried()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.ServiceUnavailable))
            .Enqueue(new SseConnection(HttpStatusCode.OK, [StatusLine("Success")]));

        var frames = await DrainAsync(NewClient(handler));

        Assert.Multiple(() =>
        {
            Assert.That(handler.ConnectionsServed, Is.EqualTo(2));
            Assert.That(frames.OfType<StreamConnectionFrame<RunStreamEvent>>()
                .Count(f => f.State == StreamConnectionState.Reconnecting), Is.EqualTo(1));
            Assert.That(frames.OfType<StreamEventFrame<RunStreamEvent>>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public void NonTransientErrorOnReconnect_Throws()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK, [ViewLine(1)], SseConnectionEnd.ThrowMidStream))
            .Enqueue(new SseConnection(HttpStatusCode.Forbidden));

        Assert.ThrowsAsync<CoreApiException>(async () => await DrainAsync(NewClient(handler)));
    }

    [Test]
    public async Task SilentStream_PastIdleTimeout_IsTreatedAsADrop()
    {
        var handler = new SseScriptHandler()
            // Keepalive comments are consumed silently; then the connection goes quiet forever.
            .Enqueue(new SseConnection(HttpStatusCode.OK, [": ping"], SseConnectionEnd.HangSilently))
            .Enqueue(new SseConnection(HttpStatusCode.OK, [StatusLine("Success")]));

        var client = NewClient(handler, new CoreClientOptions
        {
            StreamReconnectInitialBackoffSeconds = 0,
            StreamIdleTimeoutSeconds = 1
        });
        var frames = await DrainAsync(client, TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(handler.ConnectionsServed, Is.EqualTo(2),
                "silence beyond the idle timeout must count as a dead connection");
            Assert.That(frames.OfType<StreamConnectionFrame<RunStreamEvent>>()
                    .Single(f => f.State == StreamConnectionState.Reconnecting).Cause,
                Is.InstanceOf<TimeoutException>());
            Assert.That(frames.OfType<StreamEventFrame<RunStreamEvent>>().Count(), Is.EqualTo(1),
                "comment lines are keepalives, never events");
        });
    }

    [Test]
    public async Task ConnectThatNeverProducesHeaders_IsTreatedAsADrop_AndRetried()
    {
        var handler = new SseScriptHandler()
            // The wedged mode observed against a live Core: the connection is accepted but
            // response headers never arrive. Without the connect watchdog SendAsync hangs forever.
            .Enqueue(new SseConnection(HttpStatusCode.OK, ResponseDelay: Timeout.InfiniteTimeSpan))
            .Enqueue(new SseConnection(HttpStatusCode.OK, [StatusLine("Success")]));

        var client = NewClient(handler, new CoreClientOptions
        {
            StreamReconnectInitialBackoffSeconds = 0,
            StreamIdleTimeoutSeconds = 1 // also bounds the connect phase
        });
        var frames = await DrainAsync(client, TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(handler.ConnectionsServed, Is.EqualTo(2),
                "a connect with no headers within the idle timeout must count as a dead connection");
            Assert.That(frames.OfType<StreamConnectionFrame<RunStreamEvent>>()
                    .Single(f => f.State == StreamConnectionState.Reconnecting).Cause,
                Is.InstanceOf<TimeoutException>());
            Assert.That(frames.OfType<StreamEventFrame<RunStreamEvent>>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public void CallerCancellationDuringConnect_EndsTheEnumeration_NoRetry()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK, ResponseDelay: Timeout.InfiniteTimeSpan));

        // Idle timeout disabled: only the caller's token can end the hanging connect.
        var client = NewClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamRunAsync(Guid.NewGuid(), cts.Token)) { }
        });
        Assert.That(handler.ConnectionsServed, Is.EqualTo(1),
            "a caller cancel during connect exits the stream — it is never retried");
    }

    [Test]
    public void UnaryCall_IsBoundedByTheUnaryTimeout_WhileHttpClientTimeoutIsDisabled()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK, ResponseDelay: TimeSpan.FromSeconds(30)));

        var client = NewClient(handler, new CoreClientOptions { UnaryTimeoutSeconds = 1 });

        // The watchdog is a TIMEOUT, never a cancellation: long-running consumers key "host
        // shutdown" on OperationCanceledException + their own token, so a slow Core must not
        // wear that shape.
        var ex = Assert.CatchAsync<TimeoutException>(
            async () => await client.GetRunAsync(Guid.NewGuid()));
        Assert.That(ex!.InnerException, Is.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void UnaryCall_CancelledByTheCaller_ThrowsOperationCanceled_NotTimeout()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK, ResponseDelay: TimeSpan.FromSeconds(30)));
        var client = NewClient(handler, new CoreClientOptions { UnaryTimeoutSeconds = 10 });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var ex = Assert.CatchAsync<OperationCanceledException>(
            async () => await client.GetRunAsync(Guid.NewGuid(), cts.Token));
        Assert.That(ex, Is.Not.InstanceOf<TimeoutException>(),
            "the caller's own cancel keeps its shape — that IS the shutdown signal");
    }

    [Test]
    public async Task CancellingTheConsumer_ThrowsOperationCanceled()
    {
        var handler = new SseScriptHandler()
            .Enqueue(new SseConnection(HttpStatusCode.OK, [ViewLine(1)], SseConnectionEnd.HangSilently));

        using var cts = new CancellationTokenSource();
        var client = NewClient(handler);
        var seen = 0;

        var consume = Task.Run(async () =>
        {
            await foreach (var frame in client.StreamRunAsync(Guid.NewGuid(), cts.Token))
                if (frame is StreamEventFrame<RunStreamEvent>)
                {
                    Interlocked.Increment(ref seen);
                    cts.Cancel();
                }
        });

        Assert.CatchAsync<OperationCanceledException>(async () => await consume.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.CompletedTask;
        Assert.That(seen, Is.EqualTo(1));
    }
}

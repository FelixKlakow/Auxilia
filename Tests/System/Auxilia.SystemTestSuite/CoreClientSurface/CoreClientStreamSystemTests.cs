using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Testing.DummyWorkflows;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// The stream-resilience scenarios only a REAL network can prove: snapshot-first subscribes,
/// keepalives carrying a long-idle SSE socket (the class of failure the 100-second
/// <c>HttpClient.Timeout</c> stream-death belonged to — invisible to in-proc tests), and the
/// client's internal reconnect across a genuine Core.Api container restart.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class CoreClientStreamSystemTests
{
    /// <summary>
    /// Subscribing to a run that is ALREADY mid-flight answers with a status snapshot before any
    /// live event — a reconnecting client is current immediately, no transition required.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task Subscribe_MidFlight_YieldsTheStatusSnapshotFirst(CancellationToken ct)
    {
        var client = CoreClientEnvironment.CreateClient();
        var accepted = await client.RunAsync(new RunRequest(
            CoreClientEnvironment.SleepingWorkflowType,
            CoreClientEnvironment.ContextFor(CoreClientEnvironment.SleepingWorkflowType, ("SLEEP_SECONDS", "60"))), ct);
        await ClientStreamProbe.AwaitStateAsync(client, accepted.RunId, "Running", ct);

        // A FRESH subscription to the running run: Connected, then the snapshot, no waiting.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ClientStreamFrame<RunStreamEvent>? first = null, second = null;
        await foreach (var frame in client.StreamRunAsync(accepted.RunId, cts.Token))
        {
            if (first is null) { first = frame; continue; }
            second = frame;
            break;
        }

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.InstanceOf<StreamConnectionFrame<RunStreamEvent>>());
            Assert.That(((StreamConnectionFrame<RunStreamEvent>)first!).Attempt, Is.EqualTo(1));
            Assert.That(second, Is.InstanceOf<StreamEventFrame<RunStreamEvent>>());
            Assert.That(((StreamEventFrame<RunStreamEvent>)second!).Event.Kind,
                Is.EqualTo(RunStreamEvent.StatusKind), "the first event must be the status snapshot");
        });

        cts.Cancel();
        await client.CancelRunAsync(accepted.RunId, ct);
    }

    /// <summary>
    /// A stream idle for well over 100 seconds must survive on server keepalives alone: no
    /// reconnect, completion on terminal. Exactly this scenario exposed the latent
    /// <c>HttpClient.Timeout</c> stream-death — and would catch any reintroduced transport
    /// timeout or a broken keepalive loop (the client's idle timeout here is a few seconds).
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public async Task LongIdleStream_SurvivesOnKeepalives_NoReconnect(CancellationToken ct)
    {
        var client = CoreClientEnvironment.CreateClient();
        var accepted = await client.RunAsync(new RunRequest(
            CoreClientEnvironment.SleepingWorkflowType,
            CoreClientEnvironment.ContextFor(CoreClientEnvironment.SleepingWorkflowType, ("SLEEP_SECONDS", "115"))), ct);

        var started = DateTimeOffset.UtcNow;
        var (state, frames) = await ClientStreamProbe.AwaitTerminalAsync(client, accepted.RunId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo("Success"));
            Assert.That(DateTimeOffset.UtcNow - started, Is.GreaterThan(TimeSpan.FromSeconds(100)),
                "the run must actually have idled past the classic 100s timeout window");
            Assert.That(frames.ReconnectingAttempts, Is.Empty,
                "keepalives must carry the idle stream — a reconnect means they stopped or a timeout fired");
            Assert.That(frames.ConnectAttempts, Is.EqualTo(new[] { 1 }));
        });
    }

    /// <summary>
    /// A real Core.Api outage mid-stream: the client must surface Reconnecting, resubscribe by
    /// itself (Connected with a higher attempt), be current again via the snapshot, and complete
    /// on the run's terminal state — no exception ever escapes the enumeration.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public async Task Stream_ReconnectsAcrossACoreApiRestart_AndCompletes(CancellationToken ct)
    {
        var client = CoreClientEnvironment.CreateClient();
        var accepted = await client.RunAsync(new RunRequest(
            CoreClientEnvironment.SleepingWorkflowType,
            CoreClientEnvironment.ContextFor(CoreClientEnvironment.SleepingWorkflowType, ("SLEEP_SECONDS", "90"))), ct);
        await ClientStreamProbe.AwaitStateAsync(client, accepted.RunId, "Running", ct);

        // Restart the Core while the observation below is attached: the restart is triggered
        // AFTER the first Connected frame so the outage happens on a live subscription.
        var restart = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await CoreClientEnvironment.RestartCoreApiAsync();
        }, CancellationToken.None);

        var (state, frames) = await ClientStreamProbe.AwaitTerminalAsync(client, accepted.RunId, ct);
        await restart;

        Assert.Multiple(() =>
        {
            Assert.That(frames.ReconnectingAttempts, Is.Not.Empty,
                "the outage must surface as Reconnecting frames, not as an exception");
            Assert.That(frames.ConnectAttempts, Has.Some.GreaterThan(1),
                "the client must have resubscribed on its own");
            Assert.That(frames.States, Does.Contain("Running"),
                "the post-reconnect snapshot must restate the current state");
            Assert.That(state, Is.EqualTo("Success"),
                "the run outlives the Core restart (the runner never stopped) and the stream ends terminal");
        });
    }

    /// <summary>
    /// The resumable stream end to end (real SDK, runner, bus and store): a client connecting
    /// late gets the run's state and the items published before it came, in order; it leaves
    /// mid-run, the run goes on to its end, and the client comes back with the position it kept —
    /// it gets exactly the rest, the verdict (with the workflow's reason) last. A client
    /// connecting after the end gets the whole run. No item twice, none missing, none out of order.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task ALateAndAResumingClient_GetEveryItemExactlyOnce_InOrder(CancellationToken ct)
    {
        var client = CoreClientEnvironment.CreateClient();
        var accepted = await client.RunAsync(new RunRequest(
            CoreClientEnvironment.ViewSequenceWorkflowType,
            CoreClientEnvironment.ContextFor(ViewSequenceWorkflow.WorkflowName)), ct);
        const int phase = ViewSequenceWorkflow.ItemsPerPhase;

        // The first phase is published — and stored — before anyone watches.
        await AwaitStoredItemsAsync(client, accepted.RunId, phase, ct);

        var late = new List<RunStreamEvent>();
        string? kept = null;
        using (var leave = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            await foreach (var frame in client.StreamRunAsync(accepted.RunId, leave.Token))
            {
                if (frame is not StreamEventFrame<RunStreamEvent> { Event: var evt })
                    continue;
                late.Add(evt);
                if (evt is { Kind: RunStreamEvent.ViewKind, Sequence: 3 })
                {
                    kept = evt.Cursor;
                    break; // leaves mid-run
                }
            }
        }

        // While it is away the run publishes its second phase and ends.
        await client.ProvideInputAsync(accepted.RunId, "{}", ct);
        await AwaitTerminalAsync(client, accepted.RunId, ct);

        var resumed = await CollectAsync(client.StreamRunAsync(accepted.RunId, after: kept, ct), ct);
        var whole = await CollectAsync(client.StreamRunAsync(accepted.RunId, ct), ct);

        var allNumbers = Enumerable.Range(1, 2 * phase).Select(n => (long)n).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(kept, Is.Not.Null.And.Not.Empty, "every frame carries its position");
            Assert.That(late.Select(Describe), Is.EqualTo(new[] { "status:Running", "view:1", "view:2", "view:3" }),
                "a late client: where the run stands, then the stored items in order");
            Assert.That(late.Concat(resumed).Where(f => f.Kind == RunStreamEvent.ViewKind).Select(f => f.Sequence),
                Is.EqualTo(allNumbers), "across leaving and resuming: every item once, in order");
            Assert.That(resumed.Select(Describe), Is.EqualTo(
                allNumbers.Skip(3).Select(n => $"view:{n}").Append("status:Success")),
                "the resume: exactly the rest, the verdict last");
            Assert.That(resumed[^1].AsStatus()!.Reason, Is.EqualTo("completed"), "the verdict says how the run ended");
            Assert.That(whole.Select(Describe), Is.EqualTo(
                allNumbers.Select(n => $"view:{n}").Append("status:Success")),
                "after the end: the whole run, the verdict last");
            Assert.That(whole.Where(f => f.Kind == RunStreamEvent.ViewKind).Select(f =>
                    (f.AsView()!.ViewName, System.Text.Json.JsonSerializer.Deserialize<ViewSequenceWorkflow.NumberedItem>(
                        f.AsView()!.PayloadJson)!.Number)),
                Is.EqualTo(allNumbers.Select(n => (n % 2 == 0 ? ViewSequenceWorkflow.StepsView : ViewSequenceWorkflow.LogView, (int)n))),
                "one sequence across both views, in publish order");
        });
    }

    private static string Describe(RunStreamEvent evt) => evt.Kind switch
    {
        RunStreamEvent.StatusKind => $"status:{evt.AsStatus()?.State}",
        RunStreamEvent.ViewKind => $"view:{evt.Sequence}",
        _ => $"{evt.Kind}:{evt.PayloadJson}",
    };

    private static async Task<List<RunStreamEvent>> CollectAsync(
        IAsyncEnumerable<ClientStreamFrame<RunStreamEvent>> stream, CancellationToken ct)
    {
        var events = new List<RunStreamEvent>();
        await foreach (var frame in stream.WithCancellation(ct))
            if (frame is StreamEventFrame<RunStreamEvent> { Event: var evt })
                events.Add(evt);
        return events;
    }

    private static async Task AwaitStoredItemsAsync(ICoreClient client, Guid runId, int count, CancellationToken ct)
    {
        while ((await client.GetRunViewsAsync(runId, ct: ct)).Total < count)
            await Task.Delay(250, ct);
    }

    private static async Task AwaitTerminalAsync(ICoreClient client, Guid runId, CancellationToken ct)
    {
        while (await client.GetRunAsync(runId, ct) is not { } status || !RunStates.IsTerminal(status.State))
            await Task.Delay(250, ct);
    }
}

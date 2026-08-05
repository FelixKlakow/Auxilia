using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

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
}

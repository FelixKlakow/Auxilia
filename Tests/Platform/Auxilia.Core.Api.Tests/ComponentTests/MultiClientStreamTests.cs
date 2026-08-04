using System.Collections.Concurrent;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Multi-client concurrency tests over the REAL HTTP pipeline (WebApplicationFactory): many
/// simultaneous typed clients holding SSE subscriptions while events arrive from concurrent
/// publishers. Guards the async paths a single-client test never exercises: broker
/// subscribe/publish races, per-subscriber channel isolation, server-side filter correctness
/// under load, and lossless delivery once a subscription is registered.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class MultiClientStreamTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";

    private static ArtifactPersistedEvent Persisted(string artifactType, string workItemId, Guid id)
        => new(id, artifactType, "producer-wf", workItemId, Guid.NewGuid(), 1, "HASH", 1,
            DateTimeOffset.UtcNow);

    [Test]
    public async Task TenFilteredArtifactSubscribers_ConcurrentPublishers_EachSeesExactlyItsOwnSet()
    {
        const int clientCount = 10;
        const int eventsPerType = 50;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Five clients per artifact type, each its own HttpClient + typed CoreClient — real
        // parallel consumers, not one shared connection.
        var received = new ConcurrentDictionary<int, List<ArtifactStreamEvent>>();
        var ready = new List<(int Client, string Type, IAsyncEnumerator<ClientStreamFrame<ArtifactStreamEvent>> Stream)>();
        for (var i = 0; i < clientCount; i++)
        {
            var type = i % 2 == 0 ? "type-even" : "type-odd";
            ICoreClient core = new CoreClient(CreateClient());
            var stream = core.StreamArtifactEventsAsync(type, ct: cts.Token)
                .GetAsyncEnumerator(cts.Token);
            ready.Add((i, type, stream));
            received[i] = [];
        }

        // Prove every subscription is live before the measured burst: the Connected frame is
        // yielded once the server flushed headers, i.e. after the broker subscription registered.
        foreach (var r in ready)
        {
            Assert.That(await r.Stream.MoveNextAsync(), Is.True);
            Assert.That(r.Stream.Current, Is.InstanceOf<StreamConnectionFrame<ArtifactStreamEvent>>());
        }

        // The measured burst: four concurrent publishers interleaving both types.
        var evenIds = Enumerable.Range(0, eventsPerType).Select(_ => Guid.NewGuid()).ToList();
        var oddIds = Enumerable.Range(0, eventsPerType).Select(_ => Guid.NewGuid()).ToList();
        await Task.WhenAll(
            PublishAllAsync("type-even", evenIds.Take(eventsPerType / 2)),
            PublishAllAsync("type-even", evenIds.Skip(eventsPerType / 2)),
            PublishAllAsync("type-odd", oddIds.Take(eventsPerType / 2)),
            PublishAllAsync("type-odd", oddIds.Skip(eventsPerType / 2)));

        // Drain: every client reads until it holds the full set for its type.
        await Task.WhenAll(ready.Select(async r =>
        {
            var mine = new List<ArtifactStreamEvent>();
            while (mine.Count(e => e.Artifact.WorkItemId == "burst") < eventsPerType
                   && await r.Stream.MoveNextAsync())
                if (r.Stream.Current is StreamEventFrame<ArtifactStreamEvent> frame)
                    mine.Add(frame.Event);
            received[r.Client] = mine;
        }));

        foreach (var (client, type, _) in ready)
        {
            var burst = received[client].Where(e => e.Artifact.WorkItemId == "burst").ToList();
            var expected = type == "type-even" ? evenIds : oddIds;
            Assert.Multiple(() =>
            {
                Assert.That(received[client].Select(e => e.Artifact.ArtifactType).Distinct(),
                    Is.EqualTo(new[] { type }),
                    $"client {client} must NEVER see a foreign artifact type (server-side filter)");
                Assert.That(burst.Select(e => e.Artifact.Id), Is.EquivalentTo(expected),
                    $"client {client} must receive its full burst exactly once — no loss, no duplicates");
            });
        }

        foreach (var (_, _, stream) in ready)
            await stream.DisposeAsync();

        Task PublishAllAsync(string type, IEnumerable<Guid> ids) => Task.Run(async () =>
        {
            foreach (var id in ids)
                await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
                    Persisted(type, "burst", id));
        }, cts.Token);
    }

    [Test]
    public async Task TwentyConcurrentRunStreams_EventsNeverCrossRuns_AndAllClose()
    {
        const int runCount = 20;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runIds = Enumerable.Range(0, runCount).Select(_ => Guid.NewGuid()).ToList();

        // Phase 1: open all twenty SSE connections and WAIT for their headers — the endpoint
        // flushes headers only after its broker subscription is registered, so completion here
        // guarantees no published event below can be missed.
        var responses = await Task.WhenAll(runIds.Select(async runId =>
        {
            // Each subscriber is its own HttpClient — twenty genuinely parallel SSE connections.
            var response = await CreateClient().GetAsync(
                $"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (RunId: runId, Response: response);
        }));
        var readers = responses.Select(async r =>
            (r.RunId, Body: await r.Response.Content.ReadAsStringAsync(cts.Token))).ToList();

        // Phase 2: the burst, with real concurrency — one publisher task per run, all racing
        // on the shared broker.
        await Task.WhenAll(runIds.Select(runId => Task.Run(async () =>
        {
            await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
                new WorkflowStatusEvent(runId, DummyType, "Running", null, DateTimeOffset.UtcNow));
            await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
                new ViewDataMessage(runId, "log", 1, $$"""{"run":"{{runId}}"}"""));
            await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
                new WorkflowStatusEvent(runId, DummyType, "Success", null, DateTimeOffset.UtcNow));
        }, cts.Token)));

        // Every stream closes on ITS terminal event — ReadAsStringAsync completes for all 20.
        var results = await Task.WhenAll(readers);
        foreach (var (_, response) in responses)
            response.Dispose();

        foreach (var (runId, body) in results)
        {
            var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(f => System.Text.Json.JsonSerializer.Deserialize<RunStreamEvent>(
                    f["data: ".Length..], System.Text.Json.JsonSerializerOptions.Web)!)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(frames, Has.Count.EqualTo(3),
                    $"run {runId}: Running + view + Success, nothing lost, nothing foreign");
                Assert.That(frames.All(f => f.RunId == runId), Is.True,
                    $"run {runId}: events must never cross into another run's stream");
                Assert.That(frames[1].PayloadJson, Does.Contain(runId.ToString()),
                    $"run {runId}: the view frame must be its own");
            });
        }
    }

    [Test]
    public async Task ConcurrentMirrorWrites_ProduceOneRecordPerArtifact_NoDuplicatesUnderRace()
    {
        // The tracking mirror consumes the same fanout the SSE publisher does; hammer it with
        // concurrent deliveries INCLUDING re-deliveries of the same artifact (at-least-once bus
        // semantics) — the store must end up with exactly one row per artifact id.
        const int artifactCount = 100;
        var ids = Enumerable.Range(0, artifactCount).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ids.SelectMany(id => new[]
        {
            Task.Run(() => MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
                Persisted("stress", "WI-race", id))),
            Task.Run(() => MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
                Persisted("stress", "WI-race", id))) // re-delivery of the same artifact
        }));

        ICoreClient core = new CoreClient(CreateClient());
        var page = await core.QueryArtifactsAsync(new ArtifactQuery(ArtifactType: "stress", Take: 500));

        Assert.Multiple(() =>
        {
            Assert.That(page.Total, Is.EqualTo(artifactCount),
                "re-deliveries must upsert, never duplicate");
            Assert.That(page.Items.Select(a => a.Id), Is.EquivalentTo(ids));
        });
    }
}

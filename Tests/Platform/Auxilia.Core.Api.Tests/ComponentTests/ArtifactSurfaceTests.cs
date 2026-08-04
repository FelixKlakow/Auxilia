using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.AspNetCore.Hosting;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Component tests for the artifact client surface: metadata mirrored from the
/// <c>workflow.artifact-events</c> fanout is queryable, payloads download from the shared
/// payload backend, and the SSE stream is SERVER-SIDE filtered — the client-surface
/// replacement for a bus subscription (chaining libraries never touch the bus).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ArtifactSurfaceTests : CoreApiComponentTestBase
{
    private readonly string _payloadRoot =
        Path.Combine(Path.GetTempPath(), $"auxilia-artifact-surface-{Guid.NewGuid():N}");

    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.UseSetting("ArtifactStore:PayloadRoot", _payloadRoot);

    [OneTimeTearDown]
    public void DeletePayloadRoot()
    {
        if (Directory.Exists(_payloadRoot))
            Directory.Delete(_payloadRoot, recursive: true);
    }

    private static ArtifactPersistedEvent Persisted(
        string artifactType = "code-review-result", string workItemId = "WI-1",
        Guid? artifactId = null, int version = 1, long sizeBytes = 42)
        => new(artifactId ?? Guid.NewGuid(), artifactType, "producer-wf", workItemId,
            Guid.NewGuid(), version, "HASH", sizeBytes, DateTimeOffset.UtcNow);

    [Test]
    public async Task Query_ReturnsMirroredMetadata_FilteredByTypeAndWorkItem()
    {
        var client = CreateClient();
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactType: "code-review-result", workItemId: "WI-1"));
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactType: "code-review-result", workItemId: "WI-2"));
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactType: "design-doc", workItemId: "WI-1"));

        var all = await client.GetFromJsonAsync<PagedResult<ArtifactDto>>("/api/artifacts");
        Assert.That(all!.Total, Is.EqualTo(3));

        var byType = await client.GetFromJsonAsync<PagedResult<ArtifactDto>>(
            "/api/artifacts?artifactType=code-review-result");
        Assert.That(byType!.Items.Select(a => a.ArtifactType),
            Is.All.EqualTo("code-review-result"));
        Assert.That(byType.Total, Is.EqualTo(2));

        var byBoth = await client.GetFromJsonAsync<PagedResult<ArtifactDto>>(
            "/api/artifacts?artifactType=code-review-result&workItemId=WI-2");
        Assert.That(byBoth!.Items, Has.Count.EqualTo(1));
        Assert.That(byBoth.Items[0].WorkItemId, Is.EqualTo("WI-2"));
    }

    [Test]
    public async Task Query_CreatedAfterUtc_ReturnsOnlyNewerArtifacts_OldestFirst()
    {
        // The reconnect catch-up shape: everything persisted after the consumer's last-seen
        // timestamp, paged OLDEST-first so the gap drains deterministically.
        var client = CreateClient();
        var t0 = DateTimeOffset.UtcNow;
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            new ArtifactPersistedEvent(Guid.NewGuid(), "report", "wf", "WI-1",
                Guid.NewGuid(), 1, "H", 1, t0 - TimeSpan.FromMinutes(10)));
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            new ArtifactPersistedEvent(Guid.NewGuid(), "report", "wf", "WI-2",
                Guid.NewGuid(), 1, "H", 1, t0 - TimeSpan.FromMinutes(2)));
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            new ArtifactPersistedEvent(Guid.NewGuid(), "report", "wf", "WI-3",
                Guid.NewGuid(), 1, "H", 1, t0 - TimeSpan.FromMinutes(1)));

        var cutoff = Uri.EscapeDataString((t0 - TimeSpan.FromMinutes(5)).ToString("O"));
        var page = await client.GetFromJsonAsync<PagedResult<ArtifactDto>>(
            $"/api/artifacts?artifactType=report&createdAfterUtc={cutoff}");

        Assert.That(page!.Items.Select(a => a.WorkItemId), Is.EqualTo(new[] { "WI-2", "WI-3" }),
            "only artifacts after the cutoff, ordered oldest-first for deterministic paging");
    }

    [Test]
    public async Task GetById_ReturnsTheMirroredArtifact_Or404()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactId: id, version: 3, sizeBytes: 1234));

        var artifact = await client.GetFromJsonAsync<ArtifactDto>($"/api/artifacts/{id}");
        Assert.Multiple(() =>
        {
            Assert.That(artifact!.Id, Is.EqualTo(id));
            Assert.That(artifact.Version, Is.EqualTo(3));
            Assert.That(artifact.SizeBytes, Is.EqualTo(1234));
        });

        var missing = await client.GetAsync($"/api/artifacts/{Guid.NewGuid()}");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Content_DownloadsThePayload_FromTheSharedBackend()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_payloadRoot);
        await File.WriteAllTextAsync(Path.Combine(_payloadRoot, id.ToString("N")), "the payload");
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactId: id));

        var response = await client.GetAsync($"/api/artifacts/{id}/content");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("the payload"));
    }

    [Test]
    public async Task Content_IndexedButPayloadMissing_Is404_NotACrash()
    {
        // The deployment wiring gap: metadata mirrored, but the payload backend is not shared.
        var client = CreateClient();
        var id = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactId: id));

        var response = await client.GetAsync($"/api/artifacts/{id}/content");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Stream_IsServerSideFiltered_ByArtifactType()
    {
        ICoreClient core = new CoreClient(CreateClient());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var stream = core
            .StreamArtifactEventsAsync(artifactType: "code-review-result", ct: cts.Token)
            .GetAsyncEnumerator(cts.Token);

        // The Connected frame arrives once the server flushed headers — i.e. AFTER the broker
        // subscription is registered — so everything published below is guaranteed delivered.
        Assert.That(await stream.MoveNextAsync(), Is.True);
        Assert.That(stream.Current,
            Is.InstanceOf<StreamConnectionFrame<ArtifactStreamEvent>>()
                .With.Property("State").EqualTo(StreamConnectionState.Connected));

        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactType: "code-review-result", workItemId: "WI-live"));
        Assert.That((await NextEventAsync(stream)).Artifact.ArtifactType, Is.EqualTo("code-review-result"));

        // A non-matching event must be filtered SERVER-side; the next matching one arrives instead.
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactType: "design-doc", workItemId: "WI-noise"));
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactType: "code-review-result", workItemId: "WI-match"));

        var next = await NextEventAsync(stream);
        Assert.Multiple(() =>
        {
            Assert.That(next.Artifact.ArtifactType, Is.EqualTo("code-review-result"),
                "the design-doc event must never reach this subscriber");
            Assert.That(next.Artifact.WorkItemId, Is.Not.EqualTo("WI-noise"));
        });
        cts.Cancel();
    }

    private static async Task<ArtifactStreamEvent> NextEventAsync(
        IAsyncEnumerator<ClientStreamFrame<ArtifactStreamEvent>> stream)
    {
        while (await stream.MoveNextAsync())
            if (stream.Current is StreamEventFrame<ArtifactStreamEvent> frame)
                return frame.Event;
        throw new InvalidOperationException("the stream ended without the expected event");
    }

    [Test]
    public async Task Surface_RequiresAuthentication()
    {
        var anonymous = CreateAnonymousClient();
        Assert.Multiple(async () =>
        {
            Assert.That((await anonymous.GetAsync("/api/artifacts")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await anonymous.GetAsync($"/api/artifacts/{Guid.NewGuid()}/content")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await anonymous.GetAsync(
                    "/api/artifacts/stream", HttpCompletionOption.ResponseHeadersRead)).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    [Test]
    public async Task Client_QueryAndGet_RoundTripTypedDtos()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var id = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName,
            Persisted(artifactId: id, artifactType: "plan", workItemId: "WI-9"));

        var page = await core.QueryArtifactsAsync(new ArtifactQuery(ArtifactType: "plan"));
        Assert.That(page.Items.Single().Id, Is.EqualTo(id));

        Assert.That(await core.GetArtifactAsync(id), Is.Not.Null);
        Assert.That(await core.GetArtifactAsync(Guid.NewGuid()), Is.Null);
        Assert.That(await core.OpenArtifactContentAsync(Guid.NewGuid()), Is.Null,
            "unknown artifact content must be null, not an exception");
    }
}

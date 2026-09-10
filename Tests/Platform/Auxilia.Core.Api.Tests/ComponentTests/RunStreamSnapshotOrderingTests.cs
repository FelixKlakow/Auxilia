using System.Net;
using System.Net.Http.Headers;
using Auxilia.Core.Api.Data;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// <c>GET /api/runs/{id}/stream</c> must SUBSCRIBE before it reads the snapshot record: a
/// non-terminal transition landing between the two is otherwise neither in the snapshot nor on
/// the live channel — lost until the next transition. The run store is wrapped so a transition
/// can be injected exactly inside that window.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunStreamSnapshotOrderingTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";

    private HookedRunStore _runStore = null!;

    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDataAccess<CoreRunRecord>>();
            _runStore = new HookedRunStore(new InMemoryDataAccess<CoreRunRecord>());
            services.AddSingleton<IDataAccess<CoreRunRecord>>(_runStore);
        });

    [Test]
    public async Task TransitionBetweenSubscribeAndSnapshot_ReachesTheClient()
    {
        var runId = Guid.NewGuid();
        await _runStore.SaveAsync(new CoreRunRecord
        {
            Id = runId, WorkflowType = DummyType, State = "Received",
            CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        // Fires ONCE, inside the endpoint's snapshot read: the transition is published after the
        // stale record was read but before it is written as the snapshot.
        _runStore.AfterReadById = _ => MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Running", null, DateTimeOffset.UtcNow));

        var client = CreateClient();
        using var response = await client.GetAsync(
            $"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await ReadUntilAsync(response, "Running", TimeSpan.FromSeconds(5));

        Assert.That(body, Does.Contain("Received").And.Contain("Running"),
            "the stale snapshot AND the live transition from the gap must both reach the client");
    }

    private static async Task<string> ReadUntilAsync(HttpResponseMessage response, string marker, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var body = new System.Text.StringBuilder();
        try
        {
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                body.AppendLine(line);
                if (line.Contains(marker, StringComparison.Ordinal))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting for the marker — the caller asserts on what arrived.
        }
        return body.ToString();
    }

    /// <summary>Delegating run store with a one-shot hook after a by-id read.</summary>
    private sealed class HookedRunStore(IDataAccess<CoreRunRecord> inner) : IDataAccess<CoreRunRecord>
    {
        private Func<CoreRunRecord?, Task>? _afterReadById;

        public Func<CoreRunRecord?, Task>? AfterReadById
        {
            set => _afterReadById = value;
        }

        public IObservable<CoreRunRecord> EntityAdded => inner.EntityAdded;
        public IObservable<CoreRunRecord> EntityUpdated => inner.EntityUpdated;
        public IObservable<CoreRunRecord> EntityRemoved => inner.EntityRemoved;

        public Task<IQueryable<CoreRunRecord>> ReadAsync(CancellationToken cancellationToken)
            => inner.ReadAsync(cancellationToken);

        public async Task<CoreRunRecord?> ReadAsync(Guid id, CancellationToken cancellationToken)
        {
            var record = await inner.ReadAsync(id, cancellationToken);
            if (Interlocked.Exchange(ref _afterReadById, null) is { } hook)
                await hook(record);
            return record;
        }

        public Task<bool> SaveAsync(CoreRunRecord entity, CancellationToken cancellationToken)
            => inner.SaveAsync(entity, cancellationToken);

        public Task<bool> TrySaveAsync(CoreRunRecord entity, long expectedVersion, CancellationToken cancellationToken)
            => inner.TrySaveAsync(entity, expectedVersion, cancellationToken);

        public Task<bool> RemoveAsync(Guid id) => inner.RemoveAsync(id);

        public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
            => inner.RemoveAsync(id, cancellationToken);
    }
}

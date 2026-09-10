using System.Collections.Concurrent;
using Auxilia.Core.Api.Data;
using Auxilia.Messaging;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Persists each run's view items into the Core's own store (from the
/// <see cref="ViewDataMessage.ExchangeName"/> fanout, its own exclusive queue — the live
/// <see cref="RunStreamPublisher"/> gets its own copy). Capped per run
/// (<see cref="CoreApiSettings.MaxPersistedViewItemsPerRun"/>); items beyond the cap are dropped
/// and logged — large data belongs in the artifact store, never in views. Deterministic ids make
/// re-delivery an idempotent upsert. The cap is enforced from a per-run counter seeded ONCE from
/// the store on a run's first item (never a per-message scan); with N Core.Api nodes each seeds
/// from the store and counts its own share, so the cap is a soft guard whose overshoot is
/// bounded by the node count, not an exact quota. Rows of TERMINAL runs are swept
/// <see cref="CoreApiSettings.ViewRetentionDays"/> after the run ended (live runs are never
/// touched) — the read-later surface reaches back exactly that far.
/// </summary>
public sealed class RunViewTrackingService(
    IMessageBusClient bus,
    IDataAccess<CoreRunViewRecord> views,
    IDataAccess<CoreRunRecord> runs,
    IOptions<CoreApiSettings> settings,
    TimeProvider clock,
    ILogger<RunViewTrackingService> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, int> _storedPerRun = new();
    private IAsyncDisposable? _subscription;
    private CancellationTokenSource? _lifetime;
    private Task? _sweeper;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareTopicExchangeAsync(ViewDataMessage.ExchangeName, cancellationToken);
        // SHARED queue bound "#": with N Core.Api nodes, exactly one mirrors each view item into
        // the store — the live RunStreamPublisher keeps its selectively-bound per-node copy for SSE.
        _subscription = await bus.SubscribeToTopicExchangeSharedAsync<ViewDataMessage>(
            ViewDataMessage.ExchangeName, "core-api.view-tracking", "#", HandleAsync, cancellationToken);
        _lifetime = new CancellationTokenSource();
        if (settings.Value.ViewRetentionDays > 0)
            _sweeper = SweepLoopAsync(_lifetime.Token);
        logger.LogInformation("RunViewTrackingService listening on {Exchange}.", ViewDataMessage.ExchangeName);
    }

    private async Task HandleAsync(ViewDataMessage message, CancellationToken ct)
    {
        var runId = message.WorkflowInstanceId;
        if (!_storedPerRun.TryGetValue(runId, out var stored))
        {
            // First item of this run on this node: seed the counter from the store, once.
            var seeded = (await views.ReadAsync(ct)).Count(v => v.RunId == runId);
            stored = _storedPerRun.GetOrAdd(runId, seeded);
        }

        if (stored >= settings.Value.MaxPersistedViewItemsPerRun)
        {
            logger.LogWarning(
                "Run {RunId} exceeded the persisted view-item cap ({Cap}); dropping view '{View}' seq {Sequence}.",
                runId, settings.Value.MaxPersistedViewItemsPerRun, message.ViewName, message.Sequence);
            return;
        }

        var updatedExisting = await views.SaveAsync(new CoreRunViewRecord
        {
            Id = CoreRunViewRecord.IdFor(runId, message.ViewName, message.Sequence),
            RunId = runId,
            ViewName = message.ViewName,
            Sequence = message.Sequence,
            PayloadJson = message.PayloadJson,
            TimestampUtc = clock.GetUtcNow()
        }, ct);
        // A re-delivered item upserts in place — it must not consume cap.
        if (!updatedExisting)
            _storedPerRun.AddOrUpdate(runId, 1, (_, count) => count + 1);
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            do
            {
                await SweepOnceAsync(ct);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Service stop.
        }
    }

    /// <summary>
    /// Deletes the view rows of runs that reached a terminal state more than
    /// <see cref="CoreApiSettings.ViewRetentionDays"/> ago (and rows that old whose run record no
    /// longer exists). Rows of live runs are never removed. Public for tests.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken ct = default)
    {
        var retentionDays = settings.Value.ViewRetentionDays;
        if (retentionDays <= 0)
            return;
        try
        {
            var cutoff = clock.GetUtcNow().AddDays(-retentionDays);
            var runRecords = (await runs.ReadAsync(ct))
                .Select(r => new { r.Id, r.State, r.UpdatedUtc })
                .ToList();
            var expiredRuns = runRecords
                .Where(r => CoreRunStates.IsTerminal(r.State) && r.UpdatedUtc < cutoff)
                .Select(r => r.Id)
                .ToHashSet();
            var knownRuns = runRecords.Select(r => r.Id).ToHashSet();

            var expired = (await views.ReadAsync(ct))
                .Select(v => new { v.Id, v.RunId, v.TimestampUtc })
                .ToList()
                .Where(v => expiredRuns.Contains(v.RunId)
                            || (!knownRuns.Contains(v.RunId) && v.TimestampUtc < cutoff))
                .ToList();
            foreach (var row in expired)
                await views.RemoveAsync(row.Id, ct);
            foreach (var runId in expired.Select(v => v.RunId).Distinct())
                _storedPerRun.TryRemove(runId, out _);
            if (expired.Count > 0)
                logger.LogInformation(
                    "View retention sweep removed {Count} view item(s) of runs terminal for more than {Days} day(s).",
                    expired.Count, retentionDays);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "View retention sweep failed — retrying on the next interval.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
            await _lifetime.CancelAsync();
        if (_subscription is not null)
            await _subscription.DisposeAsync();
        if (_sweeper is not null)
            await _sweeper.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ContinueWith(_ => { });
    }

    public void Dispose() => _lifetime?.Dispose();
}

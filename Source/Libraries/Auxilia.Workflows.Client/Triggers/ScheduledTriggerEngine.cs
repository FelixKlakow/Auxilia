using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.Workflows.Client.Triggers;

/// <summary>
/// The embeddable interval scheduler: sweeps the trigger store and dispatches every due,
/// enabled trigger through the Core Run API on behalf of its run-as principal. Runs as an
/// <see cref="IHostedService"/> in a server host, or a desktop host calls
/// <see cref="TickAsync"/> on its own cadence — triggers fire only while some host runs.
/// A single failing trigger is logged and skipped; a failing sweep (store or Core unreachable,
/// a unary timeout) is logged and retried next tick. Only the engine's OWN token stops it.
/// </summary>
public sealed class ScheduledTriggerEngine(
    ITriggerStore store,
    ICoreClient core,
    TimeProvider timeProvider,
    IOptions<WorkflowClientOptions> options,
    ILogger<ScheduledTriggerEngine> logger) : IHostedService, IDisposable
{
    private CancellationTokenSource? _loop;
    private Task? _running;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = new CancellationTokenSource();
        _running = RunAsync(_loop.Token);
        logger.LogInformation("ScheduledTriggerEngine started. Interval={Interval}s",
            options.Value.SchedulerIntervalSeconds);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SchedulerIntervalSeconds));
        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await TickAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // The store read (or anything else outside the per-trigger guard) failed —
                    // a slow Core is a TimeoutException, not a shutdown; retry next tick.
                    logger.LogError(ex, "Scheduled trigger sweep failed — will retry next interval.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>One sweep: dispatches every enabled trigger whose interval has elapsed.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var due = (await store.GetScheduledTriggersAsync(ct))
            .Where(t => t.Enabled)
            .Where(t => t.LastDispatchedUtc is null
                        || now - t.LastDispatchedUtc >= TimeSpan.FromSeconds(t.IntervalSeconds))
            .ToList();

        foreach (var trigger in due)
        {
            try
            {
                var accepted = await DispatchAsync(core, trigger.ConfigurationId, trigger.WorkflowType,
                    trigger.Context ?? new Dictionary<string, string>(), trigger.RunAsPrincipalId, ct);
                await store.SaveAsync(trigger with { LastDispatchedUtc = now }, ct);
                logger.LogInformation(
                    "Scheduled trigger {TriggerId} dispatched. RunId={RunId}", trigger.Id, accepted.RunId);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex,
                    "Scheduled trigger {TriggerId} failed to dispatch — will retry next interval.", trigger.Id);
            }
        }
    }

    /// <summary>Shared dispatch shape: stored configuration when present, inline type otherwise.</summary>
    internal static Task<RunAccepted> DispatchAsync(
        ICoreClient core, Guid? configurationId, string? workflowType,
        IReadOnlyDictionary<string, string> context, Guid? runAsPrincipalId, CancellationToken ct)
        => configurationId is { } configId
            ? core.RunConfigurationAsync(configId, onBehalfOf: runAsPrincipalId, context: context, ct: ct)
            : core.RunAsync(new RunRequest(
                workflowType ?? throw new InvalidOperationException(
                    "a trigger needs a ConfigurationId or a WorkflowType"),
                context, RequestedBy: runAsPrincipalId), ct);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loop is null)
            return;
        await _loop.CancelAsync();
        if (_running is { } running)
            await running.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ContinueWith(_ => { });
    }

    public void Dispose() => _loop?.Dispose();
}

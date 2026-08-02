using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.Workflows.Client.Triggers;

/// <summary>
/// The embeddable artifact-chaining engine: consumes the Core's SERVER-SIDE-FILTERED artifact
/// SSE stream (one consumer per distinct artifact type among the enabled triggers — never the
/// global feed, never the message bus) and dispatches each matching trigger's follow-up
/// workflow with the artifact reference in its context. Triggers are re-read from the store
/// per event, so edits apply without a restart; ADDING a trigger for a NEW artifact type needs
/// <see cref="RefreshAsync"/> (or a host restart) to open its stream.
/// Stream drops reconnect with exponential backoff — the Core is the durable side.
/// </summary>
public sealed class ArtifactChainingEngine(
    ITriggerStore store,
    ICoreClient core,
    IOptions<WorkflowClientOptions> options,
    ILogger<ArtifactChainingEngine> logger) : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Consumer)> _consumers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = new CancellationTokenSource();
        await RefreshAsync(cancellationToken);
        logger.LogInformation("ArtifactChainingEngine started with {Count} stream consumer(s).",
            _consumers.Count);
    }

    /// <summary>
    /// Aligns the stream consumers with the CURRENT enabled trigger set: opens a filtered
    /// consumer per new artifact type, stops consumers no trigger needs anymore.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_lifetime is null)
            throw new InvalidOperationException("the engine is not started");

        var wanted = (await store.GetArtifactTriggersAsync(ct))
            .Where(t => t.Enabled)
            .Select(t => t.ArtifactType)
            .ToHashSet(StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var (type, consumer) in _consumers.Where(c => !wanted.Contains(c.Key)).ToList())
            {
                consumer.Cts.Cancel();
                _consumers.Remove(type);
            }
            foreach (var type in wanted.Where(t => !_consumers.ContainsKey(t)))
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _consumers[type] = (cts, ConsumeAsync(type, cts.Token));
            }
        }
    }

    private async Task ConsumeAsync(string artifactType, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var evt in core.StreamArtifactEventsAsync(artifactType, ct: ct))
                {
                    backoff = TimeSpan.FromSeconds(1);
                    await HandleAsync(evt, ct);
                }
                // An orderly end of the stream (e.g. the Core recycled) — reconnect.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Artifact stream for '{ArtifactType}' dropped — reconnecting in {Backoff}s.",
                    artifactType, backoff.TotalSeconds);
            }

            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var max = TimeSpan.FromSeconds(Math.Max(1, options.Value.StreamReconnectMaxBackoffSeconds));
            backoff = backoff * 2 > max ? max : backoff * 2;
        }
    }

    /// <summary>
    /// Dispatches every enabled trigger matching the event (type + optional work-item filter).
    /// Triggers are read fresh from the store, so edits apply immediately. A failing dispatch
    /// is logged and does not stop the remaining triggers or the stream.
    /// </summary>
    public async Task HandleAsync(ArtifactStreamEvent evt, CancellationToken ct = default)
    {
        var matching = (await store.GetArtifactTriggersAsync(ct))
            .Where(t => t.Enabled)
            .Where(t => string.Equals(t.ArtifactType, evt.Artifact.ArtifactType, StringComparison.Ordinal))
            .Where(t => t.WorkItemId is null
                        || string.Equals(t.WorkItemId, evt.Artifact.WorkItemId, StringComparison.Ordinal))
            .ToList();

        foreach (var trigger in matching)
        {
            var context = new Dictionary<string, string>
            {
                ["ArtifactId"] = evt.Artifact.Id.ToString("D"),
                ["ArtifactType"] = evt.Artifact.ArtifactType,
                ["WorkItemId"] = evt.Artifact.WorkItemId
            };
            foreach (var (key, value) in trigger.Context ?? new Dictionary<string, string>())
                context.TryAdd(key, value);

            try
            {
                var accepted = await ScheduledTriggerEngine.DispatchAsync(
                    core, trigger.ConfigurationId, trigger.WorkflowType, context,
                    trigger.RunAsPrincipalId, ct);
                logger.LogInformation(
                    "Artifact trigger {TriggerId} dispatched. Artifact={ArtifactType} v{Version} RunId={RunId}",
                    trigger.Id, evt.Artifact.ArtifactType, evt.Artifact.Version, accepted.RunId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Artifact trigger {TriggerId} failed to dispatch for artifact {ArtifactId}.",
                    trigger.Id, evt.Artifact.Id);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is null)
            return;
        await _lifetime.CancelAsync();
        Task[] consumers;
        lock (_gate)
        {
            consumers = _consumers.Values.Select(c => c.Consumer).ToArray();
            _consumers.Clear();
        }
        await Task.WhenAll(consumers).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .ContinueWith(_ => { });
    }

    public void Dispose() => _lifetime?.Dispose();
}

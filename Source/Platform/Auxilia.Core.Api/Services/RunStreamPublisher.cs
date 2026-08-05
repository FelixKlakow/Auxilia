using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Bridges the bus live-view feeds to the SSE <see cref="RunStreamBroker"/> with SELECTIVE
/// ingest: subscribes to the <c>workflow.status</c> and <c>workflow.views</c> topic exchanges
/// with no initial bindings and, as <see cref="IRunStreamBindingListener"/>, binds/unbinds the
/// routing keys of exactly the runs this node has an open SSE stream for — per-node ingest scales
/// with the node's audience, not with global event volume. Live push only; persistence stays with
/// the shared-queue tracking mirrors.
/// </summary>
public sealed class RunStreamPublisher(
    IMessageBusClient bus,
    RunStreamBroker broker,
    Auxilia.UniversalDataAccess.IDataAccess<Data.CoreRunRecord> runs,
    ILogger<RunStreamPublisher> logger) : IHostedService, IRunStreamBindingListener
{
    private ITopicSubscription? _statusSubscription;
    private ITopicSubscription? _viewSubscription;

    // Serializes all binding mutations and guards the audience bookkeeping below.
    // NEVER awaited from a bus consumer callback: binding RPCs ride the SAME channel the
    // consumers dispatch on, so a callback waiting on the gate while its holder awaits a
    // binding RPC wedges the channel — keepalives stop, subscribes hang. Consumer-driven
    // binding work therefore drains through _aliasWork on a background task instead.
    private readonly SemaphoreSlim _bindGate = new(1, 1);
    private readonly System.Threading.Channels.Channel<(Guid InstanceId, bool Bind)> _aliasWork =
        System.Threading.Channels.Channel.CreateUnbounded<(Guid, bool)>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Task? _aliasWorker;
    private Task? _aliasSweeper;
    private readonly Dictionary<Guid, int> _audience = new();
    private readonly HashSet<Guid> _aliasBound = new();

    // A dispatch is acknowledged with its CommandId, but the runner emits events under its own
    // WorkflowInstanceId. The claim transition carries both (and is routed under both), so every
    // event is re-published under BOTH ids — a client may observe the id the dispatch gave it.
    // View messages only ever carry the instance id, which is why a command-id audience also
    // needs the instance id BOUND (the alias binding) once the pairing is known.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid> _commandIdByInstance = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid> _instanceByCommandId = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await bus.DeclareTopicExchangeAsync(ViewDataMessage.ExchangeName, cancellationToken);

        _statusSubscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, [], HandleStatusAsync, cancellationToken);
        _viewSubscription = await bus.SubscribeToTopicExchangeAsync<ViewDataMessage>(
            ViewDataMessage.ExchangeName, [], HandleViewAsync, cancellationToken);
        broker.SetListener(this);
        _aliasWorker = Task.Run(() => DrainAliasWorkAsync(_stop.Token), CancellationToken.None);
        _aliasSweeper = Task.Run(() => SweepUnpairedAliasesAsync(_stop.Token), CancellationToken.None);

        logger.LogInformation(
            "RunStreamPublisher listening selectively on {StatusExchange} and {ViewExchange}.",
            WorkflowStatusEvent.ExchangeName, ViewDataMessage.ExchangeName);
    }

    public async Task RunSubscribedAsync(Guid runId, CancellationToken ct)
    {
        await _bindGate.WaitAsync(ct);
        try
        {
            var count = _audience.GetValueOrDefault(runId) + 1;
            _audience[runId] = count;
            if (count > 1)
                return;

            await BindLockedAsync(runId, ct);
            // The audience may have subscribed under the dispatch command id — when its instance
            // id is already known, bind that too so view items (instance-keyed only) arrive.
            if (_instanceByCommandId.TryGetValue(runId, out var instanceId))
                await BindAliasLockedAsync(instanceId, ct);
        }
        finally { _bindGate.Release(); }
    }

    public async Task RunUnsubscribedAsync(Guid runId)
    {
        try
        {
            await _bindGate.WaitAsync();
            try
            {
                var count = _audience.GetValueOrDefault(runId) - 1;
                if (count > 0)
                {
                    _audience[runId] = count;
                    return;
                }
                _audience.Remove(runId);

                // Keep the keys when the id is still needed as an alias of an open command-id
                // stream; bindings are broker-idempotent, so one removal would kill both uses.
                if (!_aliasBound.Contains(runId))
                    await UnbindLockedAsync(runId);
                if (_instanceByCommandId.TryGetValue(runId, out var instanceId)
                    && _aliasBound.Remove(instanceId) && !_audience.ContainsKey(instanceId))
                    await UnbindLockedAsync(instanceId);
            }
            finally { _bindGate.Release(); }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove bus bindings for run {RunId}.", runId);
        }
    }

    /// <summary>
    /// Records a command-id↔instance-id pairing (from the claim event, or resolved from the run
    /// store for late subscribers) and binds the instance id when the command id has an audience.
    /// </summary>
    public async Task RegisterAliasAsync(Guid commandId, Guid instanceId, CancellationToken ct = default)
    {
        if (commandId == instanceId)
            return;
        _commandIdByInstance[instanceId] = commandId;
        _instanceByCommandId[commandId] = instanceId;

        await _bindGate.WaitAsync(ct);
        try
        {
            if (_audience.ContainsKey(commandId))
                await BindAliasLockedAsync(instanceId, ct);
        }
        finally { _bindGate.Release(); }
    }

    private async Task BindLockedAsync(Guid runId, CancellationToken ct)
    {
        foreach (var key in WorkflowStatusEvent.BindingKeysFor(runId))
            await _statusSubscription!.AddBindingAsync(key, ct);
        await _viewSubscription!.AddBindingAsync(ViewDataMessage.RoutingKeyFor(runId), ct);
    }

    private async Task UnbindLockedAsync(Guid runId)
    {
        foreach (var key in WorkflowStatusEvent.BindingKeysFor(runId))
            await _statusSubscription!.RemoveBindingAsync(key);
        await _viewSubscription!.RemoveBindingAsync(ViewDataMessage.RoutingKeyFor(runId));
    }

    private async Task BindAliasLockedAsync(Guid instanceId, CancellationToken ct)
    {
        if (!_aliasBound.Add(instanceId))
            return;
        await BindLockedAsync(instanceId, ct);
    }

    private Task HandleStatusAsync(WorkflowStatusEvent statusEvent, CancellationToken ct)
    {
        // Consumer callback: dictionaries update synchronously (publishes below need them NOW),
        // the gate-guarded binding RPCs are handed to the drain task — see the _bindGate note.
        var instanceId = statusEvent.WorkflowInstanceId;
        if (statusEvent.CommandId is { } commandId && commandId != instanceId)
        {
            _commandIdByInstance[instanceId] = commandId;
            _instanceByCommandId[commandId] = instanceId;
            _aliasWork.Writer.TryWrite((instanceId, true));
        }

        PublishAliased(instanceId, runId => new RunStreamEvent(
            RunStreamEvent.StatusKind,
            runId,
            Sequence: 0,
            PayloadJson: JsonSerializer.Serialize(statusEvent, JsonSerializerOptions.Web),
            TimestampUtc: statusEvent.TimestampUtc));

        if (CoreRunStates.IsTerminal(statusEvent.State))
        {
            // Drop the pairing (no further events come) and queue the alias unbind.
            if (_commandIdByInstance.TryRemove(instanceId, out var pairedCommandId))
                _instanceByCommandId.TryRemove(pairedCommandId, out _);
            _aliasWork.Writer.TryWrite((instanceId, false));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Self-healing for the subscribe⇄claim race: a subscriber that attached between dispatch
    /// and claim relies on OBSERVING the claim event to learn its instance pairing — if the
    /// claim beats the freshly added binding, the pairing is otherwise lost and the stream
    /// starves forever while the run store is perfectly current. Re-resolve unpaired
    /// command-id audiences from the store until the pairing appears.
    /// </summary>
    private async Task SweepUnpairedAliasesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            Guid[] audienceIds;
            Guid[] unpaired;
            await _bindGate.WaitAsync(ct);
            try
            {
                audienceIds = _audience.Keys.ToArray();
                unpaired = audienceIds
                    .Where(id => !_instanceByCommandId.ContainsKey(id) && !_commandIdByInstance.ContainsKey(id))
                    .ToArray();
            }
            finally { _bindGate.Release(); }
            if (audienceIds.Length == 0)
                continue;

            try
            {
                var records = await runs.ReadAsync(ct);

                // TERMINAL BACKSTOP: an event lost in any recovery gap must never leave a
                // subscriber outliving its run — whatever id an audience watches, if the
                // store says the run is over, replay the terminal snapshot; the stream
                // completes on it, duplicates cannot exist (completion ends delivery).
                foreach (var id in audienceIds)
                {
                    var record = records.FirstOrDefault(r => r.Id == id)
                                 ?? records.FirstOrDefault(r => r.CommandId == id);
                    if (record is null || !CoreRunStates.IsTerminal(record.State))
                        continue;
                    var final = new WorkflowStatusEvent(
                        record.Id, record.WorkflowType, record.State, record.ErrorMessage,
                        record.UpdatedUtc, record.OwnerServiceId, record.CommandId,
                        record.TerminalEndpoint);
                    broker.Publish(new RunStreamEvent(
                        RunStreamEvent.StatusKind, id, Sequence: 0,
                        PayloadJson: JsonSerializer.Serialize(final, JsonSerializerOptions.Web),
                        TimestampUtc: record.UpdatedUtc));
                }
                foreach (var id in unpaired)
                {
                    if (records.FirstOrDefault(r => r.CommandId == id && r.Id != id) is not { } rekeyed)
                        continue;
                    logger.LogInformation(
                        "Recovered the {CommandId}→{InstanceId} pairing from the run store (claim event missed).",
                        id, rekeyed.Id);
                    await RegisterAliasAsync(id, rekeyed.Id, ct);

                    // The audience missed the claim transition itself — replay the record's
                    // current state so a quiet run (no transition until terminal) does not
                    // leave the subscriber on the stale Dispatched snapshot. A record that is
                    // ALREADY terminal must be replayed too: the stream completes on it —
                    // otherwise keepalives hold the orphaned subscriber open forever.
                    var recovered = new WorkflowStatusEvent(
                        rekeyed.Id, rekeyed.WorkflowType, rekeyed.State, rekeyed.ErrorMessage,
                        rekeyed.UpdatedUtc, rekeyed.OwnerServiceId, rekeyed.CommandId,
                        rekeyed.TerminalEndpoint);
                    PublishAliased(rekeyed.Id, runId => new RunStreamEvent(
                        RunStreamEvent.StatusKind, runId, Sequence: 0,
                        PayloadJson: JsonSerializer.Serialize(recovered, JsonSerializerOptions.Web),
                        TimestampUtc: rekeyed.UpdatedUtc));

                    if (CoreRunStates.IsTerminal(rekeyed.State))
                    {
                        // No further events come — drop the pairing and its alias binding again.
                        if (_commandIdByInstance.TryRemove(rekeyed.Id, out var pairedCommandId))
                            _instanceByCommandId.TryRemove(pairedCommandId, out _);
                        _aliasWork.Writer.TryWrite((rekeyed.Id, false));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Alias sweep failed; will retry.");
            }
        }
    }

    /// <summary>Applies consumer-driven alias bind/unbind requests OFF the bus dispatch path.</summary>
    private async Task DrainAliasWorkAsync(CancellationToken ct)
    {
        await foreach (var (instanceId, bind) in _aliasWork.Reader.ReadAllAsync(ct))
        {
            try
            {
                await _bindGate.WaitAsync(ct);
                try
                {
                    if (bind)
                    {
                        if (_commandIdByInstance.TryGetValue(instanceId, out var commandId)
                            && _audience.ContainsKey(commandId))
                            await BindAliasLockedAsync(instanceId, ct);
                    }
                    else if (_aliasBound.Remove(instanceId) && !_audience.ContainsKey(instanceId))
                    {
                        await UnbindLockedAsync(instanceId);
                    }
                }
                finally { _bindGate.Release(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Alias binding work failed for instance {InstanceId}.", instanceId);
            }
        }
    }

    private Task HandleViewAsync(ViewDataMessage message, CancellationToken ct)
    {
        PublishAliased(message.WorkflowInstanceId, runId => new RunStreamEvent(
            RunStreamEvent.ViewKind,
            runId,
            message.Sequence,
            PayloadJson: JsonSerializer.Serialize(message, JsonSerializerOptions.Web),
            TimestampUtc: DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    /// <summary>Publishes under the instance id and, when known, the originating command id too.</summary>
    private void PublishAliased(Guid instanceId, Func<Guid, RunStreamEvent> eventFor)
    {
        broker.Publish(eventFor(instanceId));
        if (_commandIdByInstance.TryGetValue(instanceId, out var commandId))
            broker.Publish(eventFor(commandId));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _aliasWork.Writer.TryComplete();
        _stop.Cancel();
        if (_aliasWorker is not null)
            try { await _aliasWorker; } catch (OperationCanceledException) { }
        if (_aliasSweeper is not null)
            try { await _aliasSweeper; } catch (OperationCanceledException) { }
        if (_statusSubscription is not null)
            await _statusSubscription.DisposeAsync();
        if (_viewSubscription is not null)
            await _viewSubscription.DisposeAsync();
    }
}

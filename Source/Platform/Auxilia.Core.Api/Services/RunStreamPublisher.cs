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
    ILogger<RunStreamPublisher> logger) : IHostedService, IRunStreamBindingListener
{
    private ITopicSubscription? _statusSubscription;
    private ITopicSubscription? _viewSubscription;

    // Serializes all binding mutations and guards the audience bookkeeping below.
    private readonly SemaphoreSlim _bindGate = new(1, 1);
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

    private async Task HandleStatusAsync(WorkflowStatusEvent statusEvent, CancellationToken ct)
    {
        if (statusEvent.CommandId is { } commandId && commandId != statusEvent.WorkflowInstanceId)
            await RegisterAliasAsync(commandId, statusEvent.WorkflowInstanceId, ct);

        PublishAliased(statusEvent.WorkflowInstanceId, runId => new RunStreamEvent(
            RunStreamEvent.StatusKind,
            runId,
            Sequence: 0,
            PayloadJson: JsonSerializer.Serialize(statusEvent, JsonSerializerOptions.Web),
            TimestampUtc: statusEvent.TimestampUtc));

        if (CoreRunStates.IsTerminal(statusEvent.State))
            await ForgetInstanceAsync(statusEvent.WorkflowInstanceId);
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

    /// <summary>Drops a terminal run's pairing and its alias binding (no further events come).</summary>
    private async Task ForgetInstanceAsync(Guid instanceId)
    {
        if (_commandIdByInstance.TryRemove(instanceId, out var commandId))
            _instanceByCommandId.TryRemove(commandId, out _);

        try
        {
            await _bindGate.WaitAsync();
            try
            {
                if (_aliasBound.Remove(instanceId) && !_audience.ContainsKey(instanceId))
                    await UnbindLockedAsync(instanceId);
            }
            finally { _bindGate.Release(); }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove alias bindings for instance {InstanceId}.", instanceId);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_statusSubscription is not null)
            await _statusSubscription.DisposeAsync();
        if (_viewSubscription is not null)
            await _viewSubscription.DisposeAsync();
    }
}

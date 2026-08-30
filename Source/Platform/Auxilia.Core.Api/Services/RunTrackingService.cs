using Auxilia.Core.Api.Data;
using Auxilia.Messaging;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Builds the Core's run view from the runner's lifecycle events. Subscribes to the
/// <see cref="WorkflowStatusEvent.ExchangeName"/> topic exchange on the SHARED
/// <c>core-api.run-tracking</c> queue and upserts a <see cref="CoreRunRecord"/> per instance on
/// every transition — via versioned compare-and-swap, because with N Core.Api nodes consecutive
/// transitions of the same run land on different nodes.
/// </summary>
public sealed class RunTrackingService(
    IMessageBusClient bus,
    IDataAccess<CoreRunRecord> runs,
    IDataAccess<CoreRunResolutionRecord> resolutions,
    ILogger<RunTrackingService> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        // SHARED queue bound "#": with N Core.Api nodes, exactly one processes each status
        // transition into CoreRunRecord — no N× write amplification, and the mirror sees every
        // run regardless of the selective per-node SSE bindings.
        _subscription = await bus.SubscribeToTopicExchangeSharedAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, "core-api.run-tracking", "#", HandleAsync, cancellationToken);
        logger.LogInformation(
            "RunTrackingService listening on {Exchange}.", WorkflowStatusEvent.ExchangeName);
    }

    private async Task HandleAsync(WorkflowStatusEvent statusEvent, CancellationToken ct)
    {
        // READ-CHECK-CAS LOOP: the shared queue round-robins transitions for the SAME run across
        // Core.Api nodes, so another node can save between our read and our save (e.g. it saves a
        // terminal PreFlightFailed while we process the paired non-terminal Received). The
        // conditional save detects that; on a lost swap we re-read and re-apply every guard below.
        while (true)
        {
            if (await TryApplyAsync(statusEvent, ct))
                return;
        }
    }

    /// <summary>One read-check-CAS attempt; false = the conditional save lost a race, retry.</summary>
    private async Task<bool> TryApplyAsync(WorkflowStatusEvent statusEvent, CancellationToken ct)
    {
        var existing = await runs.ReadAsync(statusEvent.WorkflowInstanceId, ct);

        // TERMINAL SINK: a terminal record never becomes non-terminal again. A late event from a
        // stale queued command or a re-adopted runner whose run the Core already failed over must
        // not resurrect the run — the Core's verdict is the single source of truth.
        if (existing is not null
            && CoreRunStates.IsTerminal(existing.State)
            && !CoreRunStates.IsTerminal(statusEvent.State))
        {
            logger.LogWarning(
                "Dropping non-terminal status {State} for run {RunId}: the run is already {Existing}.",
                statusEvent.State, statusEvent.WorkflowInstanceId, existing.State);
            return true;
        }

        // REKEY ON CLAIM: dispatch writes a Dispatched record under the COMMAND id — the only id
        // that exists before a runner claims. The claim event carries both ids; merge the dispatch
        // record onto the instance id and delete the command-keyed row. Idempotent: once rekeyed,
        // the command-keyed read finds nothing.
        CoreRunRecord? dispatchRecord = null;
        if (existing is null
            && statusEvent.CommandId is { } cid
            && cid != statusEvent.WorkflowInstanceId
            && await runs.ReadAsync(cid, ct) is { } byCommand
            && byCommand.Id == byCommand.CommandId)
        {
            if (CoreRunStates.IsTerminal(byCommand.State))
            {
                // The claim-timeout sweep already declared this dispatch dead; one source of
                // truth — the late claimant's events are dropped (its cancel is already queued).
                logger.LogWarning(
                    "Dropping status {State} for run {RunId}: its dispatch {CommandId} was already finalized as {Final}.",
                    statusEvent.State, statusEvent.WorkflowInstanceId, cid, byCommand.State);
                return true;
            }
            dispatchRecord = byCommand;
        }
        var mergeBase = existing ?? dispatchRecord;

        // Ownership + the dispatch command are stamped once (on the claim event) and then preserved —
        // later transitions may omit them. The dispatch command is recovered from the Core's own
        // resolution store via the command id, never from the runner's database.
        var ownerServiceId = statusEvent.OwnerServiceId ?? mergeBase?.OwnerServiceId;
        var dispatchCommandJson = mergeBase?.DispatchCommandJson;
        if (dispatchCommandJson is null && statusEvent.CommandId is { } commandId)
            dispatchCommandJson = (await resolutions.ReadAsync(commandId, ct))?.DispatchCommandJson;

        // Conditional save against the version just read (0 = the record must still be absent):
        // a concurrent write by another node fails the swap instead of being silently overwritten.
        var saved = await runs.TrySaveAsync(new CoreRunRecord
        {
            Id = statusEvent.WorkflowInstanceId,
            WorkflowType = statusEvent.WorkflowType,
            State = statusEvent.State,
            ErrorMessage = statusEvent.ErrorMessage,
            CreatedUtc = mergeBase?.CreatedUtc ?? statusEvent.TimestampUtc,
            UpdatedUtc = statusEvent.TimestampUtc,
            // Stamped on the FIRST terminal transition and preserved — a late duplicate event
            // must not shift the completion time.
            CompletedUtc = mergeBase?.CompletedUtc
                           ?? (CoreRunStates.IsTerminal(statusEvent.State) ? statusEvent.TimestampUtc : null),
            ConfigurationId = mergeBase?.ConfigurationId,
            ConfigurationName = mergeBase?.ConfigurationName,
            OwnerServiceId = ownerServiceId,
            CommandId = statusEvent.CommandId ?? mergeBase?.CommandId,
            DispatchCommandJson = dispatchCommandJson,
            TerminalEndpoint = statusEvent.TerminalEndpoint ?? mergeBase?.TerminalEndpoint
        }, existing?.Version ?? 0, ct);
        if (!saved)
            return false;

        if (dispatchRecord is not null)
            await runs.RemoveAsync(dispatchRecord.Id, ct);
        return true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

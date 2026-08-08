using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// The platform's built-in event publisher: translates TERMINAL run-status transitions from
/// <c>workflow.status</c> into reserved <c>run.*</c> platform events on <c>workflow.events</c>
/// (shared queue — with N Core.Api nodes exactly one translates each transition). Feeding the
/// normal event pipeline means one ingest path serves persistence, SSE, and triggers alike.
/// The event id is DETERMINISTIC per (run, event type), so bus re-delivery or duplicate
/// terminal transitions collapse to an idempotent upsert / consumer-side dedupe.
/// </summary>
public sealed class RunLifecycleEventPublisher(
    IMessageBusClient bus,
    IDataAccess<CoreRunRecord> runs,
    ILogger<RunLifecycleEventPublisher> logger) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await bus.DeclareTopicExchangeAsync(WorkflowEventMessage.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToTopicExchangeSharedAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, "core-api.run-lifecycle-events", "#", HandleAsync,
            cancellationToken);
        logger.LogInformation("RunLifecycleEventPublisher translating terminal states on {Exchange}.",
            WorkflowStatusEvent.ExchangeName);
    }

    /// <summary>Terminal state → reserved event type; null for non-terminal transitions.</summary>
    internal static string? EventTypeFor(string state) => state switch
    {
        "Success" => PlatformEventTypes.RunSucceeded,
        "Failed" or "PreFlightFailed" => PlatformEventTypes.RunFailed,
        "Cancelled" => PlatformEventTypes.RunCancelled,
        _ => null
    };

    internal async Task HandleAsync(WorkflowStatusEvent status, CancellationToken ct)
    {
        if (EventTypeFor(status.State) is not { } eventType)
            return;

        var payload = JsonSerializer.Serialize(new { state = status.State, errorMessage = status.ErrorMessage });
        await bus.PublishToTopicExchangeAsync(WorkflowEventMessage.ExchangeName,
            WorkflowEventMessage.RoutingKeyFor(eventType),
            new WorkflowEventMessage(
                DeterministicEventId(status.WorkflowInstanceId, eventType),
                eventType, status.WorkflowInstanceId, status.WorkflowType,
                WorkItemId: await ResolveWorkItemIdAsync(status.WorkflowInstanceId, ct),
                payload, status.TimestampUtc), ct);
    }

    /// <summary>
    /// Best-effort enrichment: the run's own dispatch context (recovered from the Core's stored
    /// <c>DispatchCommandJson</c>) may carry a <c>WorkItemId</c> — trigger engines inject one —
    /// so <c>run.*</c> triggers can narrow by work item. Anything missing or malformed yields
    /// empty; the translation itself never fails, and the DETERMINISTIC event id is untouched
    /// (re-delivery may enrich differently — the mirror's upsert stays idempotent by id).
    /// </summary>
    internal async Task<string> ResolveWorkItemIdAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            var record = await runs.ReadAsync(runId, ct)
                         ?? (await runs.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == runId);
            if (record?.DispatchCommandJson is not { Length: > 0 } commandJson)
                return string.Empty;
            var command = JsonSerializer.Deserialize<RunWorkflowCommand>(commandJson);
            return command?.Context
                .FirstOrDefault(entry =>
                    string.Equals(entry.Key, "WorkItemId", StringComparison.OrdinalIgnoreCase))
                .Value ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve the WorkItemId of run {RunId} — leaving it empty.", runId);
            return string.Empty;
        }
    }

    /// <summary>
    /// Ingest guard for the reserved vocabulary: a <c>run.*</c> event is accepted only when it
    /// carries the exact deterministic id THIS publisher mints for its (run, type) — a spoofed
    /// reserved event with any other id is dropped, and a correctly-computed id collapses into
    /// the platform's own idempotent upsert. Non-reserved types always pass.
    /// </summary>
    internal static bool IsAuthentic(WorkflowEventMessage evt)
        => !evt.EventType.StartsWith(WorkflowEventMessage.ReservedPlatformPrefix, StringComparison.Ordinal)
           || evt.EventId == DeterministicEventId(evt.WorkflowInstanceId, evt.EventType);

    /// <summary>Name-based id over (run, event type) — stable across re-delivery.</summary>
    internal static Guid DeterministicEventId(Guid runId, string eventType)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"auxilia-run-event:{runId:D}:{eventType}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

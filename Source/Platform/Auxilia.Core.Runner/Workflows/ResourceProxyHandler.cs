using System.Collections.Concurrent;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Audited Resource Proxy (ARCHITECTURE §8): workflows publish <see cref="ResourceRequest"/>s
/// authenticated by the instance token; the handler routes them to the registered
/// <see cref="IResourceConnector"/> and answers only on the instance's canonical response
/// queue. Every call is audited; the workflow never holds resource credentials. Calls run side
/// by side (up to <see cref="MaxConcurrentCalls"/>), so one slow call — a large checkpoint — never
/// holds up every other run's; a call is answered within <see cref="CallDeadline"/> of its
/// arrival — waiting for capacity included — so the workflow, which stops waiting a little later,
/// always hears how it ended; and one run has at most <see cref="MaxCallsPerInstance"/> in flight —
/// more are refused at once instead of taking the others' capacity.
/// </summary>
public sealed class ResourceProxyHandler(
    IMessageBusClient messageBus,
    IEnumerable<IResourceConnector> connectors,
    WorkflowInstanceTokenRegistry tokenRegistry,
    AuditLog auditLog,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
    ILogger<ResourceProxyHandler> logger,
    TimeProvider? clock = null)
{
    /// <summary>Calls in flight at once; past it the subscription waits, so the broker holds further requests back.</summary>
    internal const int MaxConcurrentCalls = 8;

    /// <summary>Calls one run may have in flight; a further one is refused right away.</summary>
    internal const int MaxCallsPerInstance = 2;

    /// <summary>
    /// How long after its arrival a call is answered at the latest — shorter than the SDK's wait
    /// (<c>ResourceProxyClient.CallTimeout</c>, 60 s), so the answer always reaches it.
    /// </summary>
    internal TimeSpan CallDeadline { get; set; } = TimeSpan.FromSeconds(50);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly SemaphoreSlim _slots = new(MaxConcurrentCalls);
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, int> _perInstance = new();
    private CancellationTokenSource _stopping = new();
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Calls end with the host as well as at their deadline.
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queueName = dispatcherSettings.Value.ResourceProxyQueueName;
        await WorkloadBusTopology.ExposeIngressQueueAsync(messageBus, queueName, ct);
        _subscription = await messageBus.SubscribeAsync<ResourceRequest>(queueName, AdmitAsync, ct);

        logger.LogInformation("ResourceProxyHandler started — listening on {QueueName}.", queueName);
    }

    /// <summary>Completes once every admitted call has finished.</summary>
    internal Task DrainAsync() => Task.WhenAll(_inFlight.Keys);

    private async Task AdmitAsync(ResourceRequest request, CancellationToken ct)
    {
        var arrived = _clock.GetTimestamp();
        // Authenticated before it takes any capacity: a request in another run's name counts for nothing.
        if (dispatcherSettings.Value.RequireInstanceToken &&
            !tokenRegistry.Validate(request.WorkflowInstanceId, request.InstanceToken))
        {
            logger.LogWarning(
                "Rejected ResourceRequest with missing or invalid instance token. InstanceId={InstanceId} Resource={ResourceName}",
                request.WorkflowInstanceId, request.ResourceName);
            await auditLog.AppendAsync(
                "steering-instance", "workflow.resource.rejected",
                request.WorkflowInstanceId.ToString(), "invalid-instance-token", ct: ct);
            return;
        }

        var instance = request.WorkflowInstanceId;
        if (_perInstance.AddOrUpdate(instance, 1, (_, calls) => calls + 1) > MaxCallsPerInstance)
        {
            Leave(instance);
            await RespondAsync(request, new ResourceResponse(
                request.RequestId, false, "the run already has resource calls in flight — retry once they answered", null));
            return;
        }

        bool admitted;
        try
        {
            admitted = await _slots.WaitAsync(CallDeadline, _stopping.Token);
        }
        catch
        {
            Leave(instance);
            throw;
        }
        if (!admitted)
        {
            Leave(instance);
            await RespondAsync(request, new ResourceResponse(
                request.RequestId, false, "the runner is too busy — no capacity freed up in time", null));
            return;
        }
        // The waiting counted: the answer is due CallDeadline after the call arrived.
        var remaining = CallDeadline - _clock.GetElapsedTime(arrived);
        var call = Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            await HandleAsync(request, deadline.Token);
        });
        _inFlight[call] = 0;
        _ = call.ContinueWith(done =>
        {
            if (done.Exception is { } failure)
                logger.LogError(failure.GetBaseException(),
                    "Resource call could not be answered. InstanceId={InstanceId} Resource={ResourceName}",
                    request.WorkflowInstanceId, request.ResourceName);
            _inFlight.TryRemove(done, out _);
            _slots.Release();
            Leave(instance);
        }, TaskScheduler.Default);
    }

    private void Leave(Guid instance)
    {
        // Drop the entry at zero, keep it otherwise — atomically, so a concurrent admission is never lost.
        var remaining = _perInstance.AddOrUpdate(instance, 0, (_, calls) => calls - 1);
        if (remaining <= 0)
            _perInstance.TryRemove(new KeyValuePair<Guid, int>(instance, remaining));
    }

    private async Task HandleAsync(ResourceRequest request, CancellationToken ct)
    {
        var connector = connectors.FirstOrDefault(c => c.ResourceName == request.ResourceName);
        if (connector is null)
        {
            logger.LogWarning(
                "ResourceRequest for unknown resource {ResourceName}. InstanceId={InstanceId}",
                request.ResourceName, request.WorkflowInstanceId);
            await RespondAsync(request, new ResourceResponse(
                request.RequestId, false, $"Unknown resource '{request.ResourceName}'", null));
            return;
        }

        await auditLog.AppendAsync(
            "steering-instance", "workflow.resource-call",
            request.WorkflowInstanceId.ToString(),
            $"{request.ResourceName}:{request.Operation}", ct: CancellationToken.None);

        try
        {
            var result = await connector.ExecuteAsync(
                request.WorkflowInstanceId, request.Operation, request.PayloadJson, ct);
            await RespondAsync(request, new ResourceResponse(request.RequestId, true, null, result));
        }
        catch (Exception ex)
        {
            var reason = ex is OperationCanceledException && ct.IsCancellationRequested
                ? $"the call did not finish within {CallDeadline.TotalSeconds:0}s"
                : ex.Message;
            logger.LogError(ex,
                "Resource call failed. InstanceId={InstanceId} Resource={ResourceName} Operation={Operation}",
                request.WorkflowInstanceId, request.ResourceName, request.Operation);
            await RespondAsync(request, new ResourceResponse(request.RequestId, false, reason, null));
        }
    }

    // The answer goes out even when the call's own token has run out.
    private Task RespondAsync(ResourceRequest request, ResourceResponse response)
        => messageBus.PublishAsync(
            WorkflowQueues.ResourceResponseQueueFor(request.WorkflowInstanceId), response, CancellationToken.None);

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
        await _stopping.CancelAsync();
        await DrainAsync().WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
    }
}

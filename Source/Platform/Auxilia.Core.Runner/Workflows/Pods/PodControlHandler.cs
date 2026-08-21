using System.Collections.Concurrent;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows.Companions;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows.Pods;

/// <summary>
/// The per-run pod-control state, registered at dispatch and consumed with the run: the
/// signed envelope, the CONFIGURATION-pinned spawnable base map (snapshotted into the
/// dispatch command — a catalog edit never changes an in-flight run), and the pod's
/// network/volume identities.
/// </summary>
public sealed record PodControlState(
    int MaxContainers,
    IReadOnlyDictionary<string, string> BaseImages,
    string NetworkName,
    IReadOnlyDictionary<string, string> VolumeNames);

/// <summary>In-memory per-runner registry of pod-control states, keyed by instance.</summary>
public sealed class PodControlRegistry
{
    private readonly ConcurrentDictionary<Guid, PodControlState> _states = new();

    public void Register(Guid instanceId, PodControlState state) => _states[instanceId] = state;

    public PodControlState? Get(Guid instanceId) => _states.GetValueOrDefault(instanceId);

    public void Consume(Guid instanceId) => _states.TryRemove(instanceId, out _);
}

/// <summary>
/// Runtime pod control (run-pod design §"pod controller"): token-authenticated
/// <see cref="PodControlRequest"/>s spawn or stop companions on the run's existing pod
/// network — every operation audited, clamped to the signed envelope, and restricted to the
/// run's configuration-pinned base images. The container never touches Docker.
/// </summary>
public sealed class PodControlHandler(
    IMessageBusClient messageBus,
    IPodHost podHost,
    PodControlRegistry registry,
    WorkflowInstanceTokenRegistry tokenRegistry,
    AuditLog auditLog,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
    ILogger<PodControlHandler> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var queueName = dispatcherSettings.Value.PodControlQueueName;
        await messageBus.DeclareQueueAsync(queueName, ct);
        _subscription = await messageBus.SubscribeAsync<PodControlRequest>(queueName, HandleAsync, ct);
        logger.LogInformation("PodControlHandler started — listening on {QueueName}.", queueName);
    }

    private async Task HandleAsync(PodControlRequest request, CancellationToken ct)
    {
        var responseTopic = WorkflowQueues.PodControlResponseQueueFor(request.WorkflowInstanceId);

        if (dispatcherSettings.Value.RequireInstanceToken &&
            !tokenRegistry.Validate(request.WorkflowInstanceId, request.InstanceToken))
        {
            logger.LogWarning(
                "Rejected PodControlRequest with missing or invalid instance token. InstanceId={InstanceId}",
                request.WorkflowInstanceId);
            await auditLog.AppendAsync(
                "core-runner", "workflow.pod-control.rejected",
                request.WorkflowInstanceId.ToString(), "invalid-instance-token", ct: ct);
            return;
        }

        async Task RefuseAsync(string reason)
        {
            await auditLog.AppendAsync(
                "core-runner", "workflow.pod-control.refused",
                request.WorkflowInstanceId.ToString(), reason, ct: ct);
            await messageBus.PublishAsync(responseTopic,
                new PodControlResponse(request.RequestId, false, reason, null), ct);
        }

        if (registry.Get(request.WorkflowInstanceId) is not { } state)
        {
            await RefuseAsync("this run has no pod-control state (no declared envelope)");
            return;
        }

        CompanionSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<CompanionSpec>(request.SpecJson, JsonOptions);
        }
        catch (JsonException)
        {
            spec = null;
        }
        if (spec is null || string.IsNullOrWhiteSpace(spec.Name))
        {
            await RefuseAsync("the request carries no parseable companion spec");
            return;
        }

        switch (request.Action)
        {
            case PodControlRequest.Spawn:
                await HandleSpawnAsync(request, state, spec, responseTopic, RefuseAsync, ct);
                return;
            case PodControlRequest.Stop:
            {
                var removed = await podHost.StopCompanionAsync(request.WorkflowInstanceId, spec.Name, ct);
                await auditLog.AppendAsync(
                    "core-runner", "workflow.pod-control.stop",
                    request.WorkflowInstanceId.ToString(), spec.Name, ct: ct);
                if (removed)
                    await messageBus.PublishAsync(responseTopic,
                        new PodControlResponse(request.RequestId, true, null, null), ct);
                else
                    await RefuseAsync($"companion '{spec.Name}' does not exist");
                return;
            }
            default:
                await RefuseAsync($"unknown pod-control action '{request.Action}'");
                return;
        }
    }

    private async Task HandleSpawnAsync(
        PodControlRequest request, PodControlState state, CompanionSpec spec,
        string responseTopic, Func<string, Task> refuseAsync, CancellationToken ct)
    {
        // The base must resolve in the run's configuration-pinned map — never an arbitrary
        // image reference, and never the live catalog.
        if (!state.BaseImages.TryGetValue(spec.Base, out var image))
        {
            await refuseAsync(
                $"base '{spec.Base}' is not among this run's configured spawnable bases "
                + $"({string.Join(", ", state.BaseImages.Keys.Order(StringComparer.Ordinal))})");
            return;
        }

        // The signed envelope clamps RUNTIME spawns only — declared companion templates are
        // separately signed topology and never consume it (matching the advertised
        // MaxPodContainers = templates + envelope). A stopped runtime companion frees its slot.
        var live = await podHost.CountRuntimeCompanionsAsync(request.WorkflowInstanceId, ct);
        if (live >= state.MaxContainers)
        {
            await refuseAsync(
                $"the pod already holds {live} of {state.MaxContainers} permitted runtime companions");
            return;
        }

        var volumeBinds = new List<string>();
        foreach (var (volumeName, mountPath) in spec.VolumeMounts ?? new Dictionary<string, string>())
        {
            if (!state.VolumeNames.TryGetValue(volumeName, out var dockerVolume)
                || string.IsNullOrWhiteSpace(mountPath) || !mountPath.StartsWith('/'))
            {
                await refuseAsync($"volume mount '{volumeName}' is not a declared pod volume (or its path is not absolute)");
                return;
            }
            volumeBinds.Add($"{dockerVolume}:{mountPath}");
        }

        var planned = new PlannedCompanion(
            spec.Name, spec.Name, image,
            spec.EnvironmentVariables ?? new Dictionary<string, string>(),
            spec.Readiness, spec.MemoryMb, spec.Cpus, volumeBinds)
        {
            Command = spec.Command
        };

        await auditLog.AppendAsync(
            "core-runner", "workflow.pod-control.spawn",
            request.WorkflowInstanceId.ToString(),
            $"{spec.Name} from {spec.Base}",
            JsonSerializer.Serialize(new { image, live = live + 1, max = state.MaxContainers }), ct);
        try
        {
            await podHost.SpawnCompanionAsync(
                request.WorkflowInstanceId, state.NetworkName, planned, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Runtime spawn failed. InstanceId={InstanceId} Companion={Companion}",
                request.WorkflowInstanceId, spec.Name);
            await refuseAsync($"spawn failed: {ex.Message}");
            return;
        }

        var endpoint = spec.Readiness is { } probe ? $"{spec.Name}:{probe.Port}" : null;
        await messageBus.PublishAsync(responseTopic, new PodControlResponse(
            request.RequestId, true, null,
            JsonSerializer.Serialize(new SpawnedCompanion(spec.Name, endpoint))), ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

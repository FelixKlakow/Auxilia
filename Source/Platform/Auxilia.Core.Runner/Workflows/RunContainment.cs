using System.Collections.Concurrent;
using System.Text.Json;
using Auxilia.Core.Runner.Workflows.Egress;
using Auxilia.PlatformData;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Everything that confines ONE run beyond its container and leaves evidence of what it did:
/// its own broker identity, its egress gateway (with the live audit of every egress attempt and
/// the optional denial kill switch), and the persisted post-mortem logs (the egress log, the
/// workflow container's own output). Released identically on every terminal path.
/// </summary>
public sealed class RunContainment(
    WorkloadBusCredentials busCredentials,
    IEgressHost egressHost,
    ArtifactPersister artifactPersister,
    WorkflowInstanceRegistry instanceRegistry,
    AuditLog auditLog,
    IOptions<WorkflowDispatcherSettings> settings,
    ILogger<RunContainment> logger)
{
    private readonly ConcurrentDictionary<Guid, EgressTally> _tallies = new();

    /// <summary>Raised once when a run exceeds <see cref="WorkflowDispatcherSettings.EgressDenialLimit"/>.</summary>
    public event Func<Guid, int, Task>? EgressDenialLimitExceeded;

    public WorkloadBusCredentials Bus => busCredentials;

    public IEgressHost Egress => egressHost;

    /// <summary>
    /// The live audit of a run's egress: every denial is audited the moment it happens (they are
    /// breakout signals), each allowed destination once (first use); past the configured denial
    /// limit the run is reported for termination.
    /// </summary>
    public async Task OnEgressEventAsync(Guid instanceId, EgressEvent evt)
    {
        if (evt.Event != "connect" || evt.Decision is null)
            return;
        var tally = _tallies.GetOrAdd(instanceId, _ => new EgressTally());
        var destination = $"{evt.Host}:{evt.Port}";
        if (evt.Decision == "deny")
        {
            var denials = Interlocked.Increment(ref tally.Denials);
            await auditLog.AppendAsync("core-runner", "workflow.egress.denied", instanceId.ToString(), destination,
                JsonSerializer.Serialize(new { kind = evt.Kind, reason = evt.Reason, denials }));
            var limit = settings.Value.EgressDenialLimit;
            if (limit > 0 && denials == limit && EgressDenialLimitExceeded is { } handler)
                await handler(instanceId, denials);
        }
        else if (evt.Decision == "allow" && tally.Allowed.TryAdd(destination, 0))
        {
            await auditLog.AppendAsync("core-runner", "workflow.egress.allowed", instanceId.ToString(), destination,
                JsonSerializer.Serialize(new { kind = evt.Kind }));
        }
    }

    /// <summary>
    /// Releases the run's containment: the broker identity always; the egress topology (its log
    /// persisted as the <c>egress-log</c> artifact) once the workflow container is gone.
    /// Best-effort and idempotent — every failure is logged, never thrown.
    /// </summary>
    public async Task ReleaseAsync(Guid instanceId, bool containerGone, CancellationToken ct = default)
    {
        await busCredentials.RevokeAsync(instanceId);
        if (!containerGone)
            return;
        try
        {
            var log = await egressHost.TeardownAsync(instanceId, ct);
            if (_tallies.TryRemove(instanceId, out var tally) || log is not null)
                await auditLog.AppendAsync("core-runner", "workflow.egress.summary", instanceId.ToString(),
                    $"{tally?.Allowed.Count ?? 0} destination(s) used, {tally?.Denials ?? 0} denied attempt(s)",
                    JsonSerializer.Serialize(new { allowed = tally?.Allowed.Keys.Order().ToList(), denials = tally?.Denials ?? 0 }),
                    ct);
            if (log is { Length: > 0 })
                await PersistLogAsync(instanceId, "egress-log", log, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Releasing the egress topology of {InstanceId} failed.", instanceId);
        }
    }

    /// <summary>Persists the workflow container's own output (bounded) as the <c>container-log</c> artifact.</summary>
    public async Task PersistContainerLogAsync(Guid instanceId, string? log, CancellationToken ct = default)
    {
        if (log is not { Length: > 0 })
            return;
        try
        {
            await PersistLogAsync(instanceId, "container-log", log, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Persisting the container log of {InstanceId} failed.", instanceId);
        }
    }

    private async Task PersistLogAsync(Guid instanceId, string artifactType, string content, CancellationToken ct)
    {
        var record = await instanceRegistry.GetAsync(instanceId, ct);
        var workItemId = string.Empty;
        if (record?.DispatchCommandJson is { } json)
            JsonSerializer.Deserialize<RunWorkflowCommand>(json)?.Context.TryGetValue("WorkItemId", out workItemId!);
        await artifactPersister.PersistRunLogsAsync(instanceId, record?.WorkflowType ?? "unknown",
            [new RunLog(artifactType, content)], workItemId ?? string.Empty, ct);
    }

    private sealed class EgressTally
    {
        public int Denials;
        public readonly ConcurrentDictionary<string, byte> Allowed = new();
    }
}

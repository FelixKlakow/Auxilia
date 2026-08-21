using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Artifacts;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Persists a successful run's declared outputs into the artifact store (ARCHITECTURE §4):
/// what a workflow may produce is bounded by its manifest declarations — only declared
/// outputs present in the run's output directory are persisted. Each persisted artifact is
/// audited and announced via <see cref="ArtifactPersistedEvent"/> (reference + hash only).
/// </summary>
public sealed class ArtifactPersister(
    IArtifactStore artifactStore,
    IMessageBusClient messageBus,
    AuditLog auditLog,
    TimeProvider timeProvider,
    IOptions<WorkflowDispatcherSettings> settings,
    ILogger<ArtifactPersister> logger)
{
    private bool _exchangeDeclared;

    /// <summary>Host-side output directory convention for a run.</summary>
    public string OutputDirectoryFor(Guid instanceId)
        => Path.Combine(settings.Value.RunOutputDirectory, instanceId.ToString("N"));

    public async Task PersistOutputsAsync(
        Guid instanceId, string workflowType, string? outputsJson, string workItemId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(outputsJson))
            return;

        var declared = JsonSerializer.Deserialize<List<WorkflowOutputDescriptor>>(outputsJson) ?? [];
        if (declared.Count == 0)
            return;

        var outputDir = OutputDirectoryFor(instanceId);
        if (!Directory.Exists(outputDir))
        {
            logger.LogInformation(
                "No output directory for run {InstanceId} — nothing to persist.", instanceId);
            return;
        }

        foreach (var output in declared)
        {
            var path = Path.GetFullPath(Path.Combine(outputDir, output.RelativePath));
            var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir))
                + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Declared output '{Name}' escapes the output directory — skipping.", output.Name);
                continue;
            }

            if (!File.Exists(path))
            {
                logger.LogInformation(
                    "Declared output '{Name}' was not produced by run {InstanceId}.",
                    output.Name, instanceId);
                continue;
            }

            await using var payload = File.OpenRead(path);
            var record = await artifactStore.SaveAsync(
                output.Name, workflowType, workItemId, instanceId, payload, ct);

            await auditLog.AppendAsync("steering-instance", "artifact.persisted",
                instanceId.ToString(), $"{output.Name} v{record.Version}", ct: ct);

            if (!_exchangeDeclared)
            {
                await messageBus.DeclareTopicExchangeAsync(ArtifactPersistedEvent.ExchangeName, ct);
                _exchangeDeclared = true;
            }
            await messageBus.PublishToTopicExchangeAsync(ArtifactPersistedEvent.ExchangeName,
                ArtifactPersistedEvent.RoutingKeyFor(record.ArtifactType),
                new ArtifactPersistedEvent(
                    record.Id, record.ArtifactType, record.WorkflowType, record.WorkItemId,
                    record.RunInstanceId, record.Version, record.ContentHash, record.SizeBytes,
                    timeProvider.GetUtcNow()), ct);

            logger.LogInformation(
                "Artifact persisted. Run={InstanceId} Type={ArtifactType} Version={Version} Hash={Hash}",
                instanceId, record.ArtifactType, record.Version, record.ContentHash);
        }

        try
        {
            Directory.Delete(outputDir, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not clean up output directory for run {InstanceId}.", instanceId);
        }
    }

    /// <summary>
    /// Persists the captured log tail of each torn-down companion as a
    /// <c>companion-log-&lt;name&gt;</c> artifact — the post-mortem evidence of the run's pod.
    /// Audited and announced like declared outputs, so the Core's mirror serves them to
    /// clients (chaining on them is possible but nothing wires it by default).
    /// </summary>
    public async Task PersistCompanionLogsAsync(
        Guid instanceId, string workflowType,
        IReadOnlyList<Pods.CompanionLog> companionLogs, string workItemId,
        CancellationToken ct = default)
    {
        foreach (var log in companionLogs)
        {
            if (log.LogTail is not { Length: > 0 } tail)
                continue;
            using var payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(tail));
            var record = await artifactStore.SaveAsync(
                $"companion-log-{log.InstanceName}", workflowType, workItemId, instanceId, payload, ct);
            await auditLog.AppendAsync("core-runner", "artifact.persisted",
                instanceId.ToString(), $"{record.ArtifactType} v{record.Version}", ct: ct);

            if (!_exchangeDeclared)
            {
                await messageBus.DeclareTopicExchangeAsync(ArtifactPersistedEvent.ExchangeName, ct);
                _exchangeDeclared = true;
            }
            await messageBus.PublishToTopicExchangeAsync(ArtifactPersistedEvent.ExchangeName,
                ArtifactPersistedEvent.RoutingKeyFor(record.ArtifactType),
                new ArtifactPersistedEvent(
                    record.Id, record.ArtifactType, record.WorkflowType, record.WorkItemId,
                    record.RunInstanceId, record.Version, record.ContentHash, record.SizeBytes,
                    timeProvider.GetUtcNow()), ct);
        }
    }
}

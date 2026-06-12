using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.BackendService.Dashboard;

/// <summary>
/// Operator CRUD over the platform's trigger records (ARCHITECTURE §6): interval schedules
/// for the <see cref="PlatformHost.TriggerScheduler"/> and artifact-completion chaining rules
/// for the <see cref="PlatformHost.ArtifactTriggerHandler"/>. Input is validated exactly as
/// those consumers expect it; every mutation is audited.
/// </summary>
public sealed class TriggerAdministration(
    IDataAccess<ScheduledTriggerRecord> scheduledTriggers,
    IDataAccess<ArtifactTriggerRecord> artifactTriggers,
    AuditLog auditLog)
{
    public async Task<IReadOnlyList<ScheduledTriggerRecord>> ListScheduledAsync(CancellationToken ct = default)
    {
        var query = await scheduledTriggers.ReadAsync(ct);
        return query.ToList().OrderBy(t => t.WorkflowType).ToList();
    }

    public async Task<ScheduledTriggerRecord> SaveScheduledAsync(
        string actor, ScheduledTriggerRecord trigger, CancellationToken ct = default)
    {
        ValidateScheduled(trigger);
        var record = trigger.Id == Guid.Empty ? trigger with { Id = Guid.NewGuid() } : trigger;
        var updated = await scheduledTriggers.SaveAsync(record, ct);
        await auditLog.AppendAsync(actor,
            updated ? "trigger.scheduled.updated" : "trigger.scheduled.created",
            record.Id.ToString(), record.WorkflowType, ct: ct);
        return record;
    }

    public async Task<bool> DeleteScheduledAsync(string actor, Guid id, CancellationToken ct = default)
    {
        if (!await scheduledTriggers.RemoveAsync(id, ct))
            return false;

        await auditLog.AppendAsync(actor, "trigger.scheduled.deleted", id.ToString(), "deleted", ct: ct);
        return true;
    }

    public async Task<IReadOnlyList<ArtifactTriggerRecord>> ListArtifactAsync(CancellationToken ct = default)
    {
        var query = await artifactTriggers.ReadAsync(ct);
        return query.ToList().OrderBy(t => t.ArtifactType).ThenBy(t => t.WorkflowType).ToList();
    }

    public async Task<ArtifactTriggerRecord> SaveArtifactAsync(
        string actor, ArtifactTriggerRecord trigger, CancellationToken ct = default)
    {
        ValidateArtifact(trigger);
        // Deterministic ID: one chaining rule per (artifact type, workflow type) pair.
        var record = trigger with { Id = ArtifactTriggerRecord.IdFor(trigger.ArtifactType, trigger.WorkflowType) };
        var updated = await artifactTriggers.SaveAsync(record, ct);
        await auditLog.AppendAsync(actor,
            updated ? "trigger.artifact.updated" : "trigger.artifact.created",
            record.Id.ToString(), $"{record.ArtifactType} → {record.WorkflowType}", ct: ct);
        return record;
    }

    public async Task<bool> DeleteArtifactAsync(string actor, Guid id, CancellationToken ct = default)
    {
        if (!await artifactTriggers.RemoveAsync(id, ct))
            return false;

        await auditLog.AppendAsync(actor, "trigger.artifact.deleted", id.ToString(), "deleted", ct: ct);
        return true;
    }

    /// <summary>The scheduler dispatches on whole-second intervals with a string-map context.</summary>
    private static void ValidateScheduled(ScheduledTriggerRecord trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger.WorkflowType))
            throw new ArgumentException("Workflow type is required.");
        if (string.IsNullOrWhiteSpace(trigger.WorkflowPackageUri))
            throw new ArgumentException("Workflow package URI is required.");
        if (trigger.IntervalSeconds < 1)
            throw new ArgumentException("Interval must be at least 1 second.");
        if (string.IsNullOrWhiteSpace(trigger.ContextJson))
            return;

        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, string>>(trigger.ContextJson) is null)
                throw new ArgumentException("Context must be a JSON object of string values.");
        }
        catch (JsonException)
        {
            throw new ArgumentException("Context must be a JSON object of string values.");
        }
    }

    private static void ValidateArtifact(ArtifactTriggerRecord trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger.ArtifactType))
            throw new ArgumentException("Artifact type is required.");
        if (string.IsNullOrWhiteSpace(trigger.WorkflowType))
            throw new ArgumentException("Workflow type is required.");
        if (string.IsNullOrWhiteSpace(trigger.WorkflowPackageUri))
            throw new ArgumentException("Workflow package URI is required.");
    }
}

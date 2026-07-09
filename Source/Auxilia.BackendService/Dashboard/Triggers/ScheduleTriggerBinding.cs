using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;

namespace Auxilia.BackendService.Dashboard.Triggers;

/// <summary>Interval-based triggers, stored as <see cref="ScheduledTriggerRecord"/>s and fired by the platform scheduler.</summary>
public sealed class ScheduleTriggerBinding(
    IDataAccess<ScheduledTriggerRecord> triggers,
    AuditLog auditLog,
    TimeProvider timeProvider) : ITriggerKindBinding
{
    public const string IntervalSecondsKey = "IntervalSeconds";
    public const string ContextJsonKey = "ContextJson";

    public TriggerKindDescriptor Descriptor { get; } = new(
        TriggerDeclaration.Schedule, "Schedule", "Starts a run on a fixed interval.",
        [
            new SettingDescriptor(IntervalSecondsKey, "Interval (seconds)", SettingKind.Number,
                Required: true, DefaultValue: "3600"),
            new SettingDescriptor(ContextJsonKey, "Context (optional)", SettingKind.Text,
                HelpText: "Key/value JSON map handed to every scheduled run.")
        ]);

    public IEnumerable<string> Validate(TriggerDraft trigger, TriggerValidationContext context)
    {
        if (!int.TryParse(trigger.Settings.GetValueOrDefault(IntervalSecondsKey), out var interval)
            || interval < 1)
            yield return "Schedule interval must be at least 1 second.";

        var contextJson = trigger.Settings.GetValueOrDefault(ContextJsonKey);
        if (string.IsNullOrWhiteSpace(contextJson))
            yield break;
        var valid = false;
        try
        {
            valid = JsonSerializer.Deserialize<Dictionary<string, string>>(contextJson) is not null;
        }
        catch (JsonException)
        {
        }
        if (!valid)
            yield return "Schedule context must be a JSON object of string values.";
    }

    public async Task<IReadOnlyList<TriggerDraft>> LoadAsync(Guid configurationId, CancellationToken ct)
        => (await triggers.ReadAsync(ct))
            .Where(t => t.WorkflowConfigurationId == configurationId).ToList()
            .Select(t =>
            {
                var draft = new TriggerDraft { ExistingId = t.Id, Kind = Descriptor.Kind };
                draft.Settings[IntervalSecondsKey] = t.IntervalSeconds.ToString();
                draft.Settings[ContextJsonKey] = t.ContextJson ?? "";
                return draft;
            })
            .ToList();

    public async Task SaveAsync(string actor, Guid? actorPrincipalId, WorkflowConfigurationDraft draft,
        Guid configurationId, TriggerDraft trigger, CancellationToken ct)
    {
        var existing = trigger.ExistingId is { } id ? await triggers.ReadAsync(id, ct) : null;
        var contextJson = trigger.Settings.GetValueOrDefault(ContextJsonKey);
        var record = new ScheduledTriggerRecord
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            WorkflowType = draft.WorkflowType.Trim(),
            WorkflowPackageUri = draft.PackageUri.Trim(),
            IntervalSeconds = int.Parse(trigger.Settings[IntervalSecondsKey]),
            ContextJson = string.IsNullOrWhiteSpace(contextJson) ? null : contextJson.Trim(),
            Enabled = draft.Enabled,
            RunAsPrincipalId = actorPrincipalId ?? existing?.RunAsPrincipalId,
            // Kept so editing a schedule never causes a surprise dispatch.
            LastDispatchedUtc = existing?.LastDispatchedUtc,
            WorkflowConfigurationId = configurationId
        };
        await triggers.SaveAsync(record, ct);
        await auditLog.AppendAsync(actor,
            existing is not null ? "trigger.scheduled.updated" : "trigger.scheduled.created",
            record.Id.ToString(), record.WorkflowType, ct: ct);
    }

    public async Task RemoveExceptAsync(
        string actor, Guid configurationId, IReadOnlySet<Guid> keptIds, CancellationToken ct)
    {
        foreach (var trigger in (await triggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId && !keptIds.Contains(t.Id)).ToList())
        {
            if (await triggers.RemoveAsync(trigger.Id, ct))
                await auditLog.AppendAsync(actor, "trigger.scheduled.deleted", trigger.Id.ToString(), "deleted", ct: ct);
        }
    }

    public async Task SetEnabledAsync(Guid configurationId, bool enabled, CancellationToken ct)
    {
        foreach (var trigger in (await triggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId).ToList())
            await triggers.SaveAsync(trigger with { Enabled = enabled }, ct);
    }

    public async Task<IReadOnlyList<ConfiguredTrigger>> ListConfiguredAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        return (await triggers.ReadAsync(ct)).ToList()
            .Where(t => t.WorkflowConfigurationId is not null)
            .Select(t =>
            {
                var (health, failing) = Health(t, now);
                return new ConfiguredTrigger(
                    t.WorkflowConfigurationId!.Value, Descriptor.Kind,
                    $"Schedule · every {TimeText.Interval(t.IntervalSeconds)}",
                    $"every {TimeText.Interval(t.IntervalSeconds)}",
                    health, failing);
            })
            .ToList();
    }

    /// <summary>When a schedule fires next, from its dispatch bookkeeping.</summary>
    internal static (string Text, bool Failing) Health(ScheduledTriggerRecord schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled)
            return ("disabled", false);
        if (schedule.LastDispatchedUtc is null)
            return ("due now", false);
        var due = schedule.LastDispatchedUtc.Value.AddSeconds(schedule.IntervalSeconds);
        return due <= now
            ? ("due now", false)
            : ($"next due in {TimeText.Interval((int)(due - now).TotalSeconds)}", false);
    }
}

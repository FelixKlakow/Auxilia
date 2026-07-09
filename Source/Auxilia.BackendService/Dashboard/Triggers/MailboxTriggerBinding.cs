using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;

namespace Auxilia.BackendService.Dashboard.Triggers;

/// <summary>Mailbox-polling triggers: each unseen (filtered) mail in an email slot instance starts a run.</summary>
public sealed class MailboxTriggerBinding(
    IDataAccess<MailboxTriggerRecord> triggers,
    IDataAccess<SlotInstanceRecord> slotInstances,
    IDataAccess<TriggerHealthRecord> triggerHealth,
    AuditLog auditLog) : ITriggerKindBinding
{
    public const string MailboxInstanceIdKey = "MailboxInstanceId";
    public const string PollIntervalSecondsKey = "PollIntervalSeconds";
    public const string SubjectContainsKey = "SubjectContains";
    public const string FromContainsKey = "FromContains";

    public TriggerKindDescriptor Descriptor { get; } = new(
        TriggerDeclaration.Mailbox, "Mailbox",
        "Polls a mailbox slot instance; each unseen matching mail starts a run.",
        [
            new SettingDescriptor(MailboxInstanceIdKey, "Mailbox", SettingKind.SlotInstance, Required: true,
                HelpText: "Each unseen mail in this mailbox starts a run (subject and body become the run's context).")
                { InstanceCategory = "task-source" },
            new SettingDescriptor(PollIntervalSecondsKey, "Poll interval (seconds)", SettingKind.Number,
                Required: true, DefaultValue: "15"),
            new SettingDescriptor(SubjectContainsKey, "Subject filter (optional)", SettingKind.Text,
                HelpText: "Only mails whose subject contains this text start a run."),
            new SettingDescriptor(FromContainsKey, "Sender filter (optional)", SettingKind.Text,
                HelpText: "Only mails from matching senders start a run; others are marked read and skipped.")
        ]);

    public IEnumerable<string> Validate(TriggerDraft trigger, TriggerValidationContext context)
    {
        if (!Guid.TryParse(trigger.Settings.GetValueOrDefault(MailboxInstanceIdKey), out var instanceId))
        {
            yield return "Pick the mailbox slot instance the mailbox trigger polls.";
            yield break;
        }
        if (!context.InstancesById.TryGetValue(instanceId, out var instance))
        {
            yield return "The mailbox trigger's slot instance no longer exists.";
            yield break;
        }
        if (context.ActorPrincipalId is { } actor && !InstanceAccessible(instance, actor))
            yield return "You have no access to the mailbox trigger's slot instance.";
        if (!int.TryParse(trigger.Settings.GetValueOrDefault(PollIntervalSecondsKey), out var interval)
            || interval < 1)
            yield return "Mailbox poll interval must be at least 1 second.";
    }

    private static bool InstanceAccessible(SlotInstanceRecord instance, Guid principalId)
        => instance.Scope == SlotInstanceScope.Company
           || instance.OwnerPrincipalId == principalId
           || (JsonSerializer.Deserialize<List<Guid>>(instance.AssignedPrincipalIdsJson) ?? [])
               .Contains(principalId);

    public async Task<IReadOnlyList<TriggerDraft>> LoadAsync(Guid configurationId, CancellationToken ct)
        => (await triggers.ReadAsync(ct))
            .Where(t => t.WorkflowConfigurationId == configurationId).ToList()
            .Select(t =>
            {
                var draft = new TriggerDraft { ExistingId = t.Id, Kind = Descriptor.Kind };
                draft.Settings[MailboxInstanceIdKey] = t.SlotInstanceId.ToString();
                draft.Settings[PollIntervalSecondsKey] = t.PollIntervalSeconds.ToString();
                draft.Settings[SubjectContainsKey] = t.SubjectContains;
                draft.Settings[FromContainsKey] = t.FromContains;
                return draft;
            })
            .ToList();

    public async Task SaveAsync(string actor, Guid? actorPrincipalId, WorkflowConfigurationDraft draft,
        Guid configurationId, TriggerDraft trigger, CancellationToken ct)
    {
        var existing = trigger.ExistingId is { } id ? await triggers.ReadAsync(id, ct) : null;
        var record = new MailboxTriggerRecord
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            WorkflowConfigurationId = configurationId,
            SlotInstanceId = Guid.Parse(trigger.Settings[MailboxInstanceIdKey]),
            PollIntervalSeconds = int.Parse(trigger.Settings[PollIntervalSecondsKey]),
            Enabled = draft.Enabled,
            RunAsPrincipalId = actorPrincipalId ?? existing?.RunAsPrincipalId,
            SubjectContains = trigger.Settings.GetValueOrDefault(SubjectContainsKey, "").Trim(),
            FromContains = trigger.Settings.GetValueOrDefault(FromContainsKey, "").Trim()
        };
        await triggers.SaveAsync(record, ct);
        await auditLog.AppendAsync(actor,
            existing is not null ? "trigger.mailbox.updated" : "trigger.mailbox.created",
            record.Id.ToString(), record.SlotInstanceId.ToString(), ct: ct);
    }

    public async Task RemoveExceptAsync(
        string actor, Guid configurationId, IReadOnlySet<Guid> keptIds, CancellationToken ct)
    {
        foreach (var trigger in (await triggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId && !keptIds.Contains(t.Id)).ToList())
        {
            if (await triggers.RemoveAsync(trigger.Id, ct))
                await auditLog.AppendAsync(actor, "trigger.mailbox.deleted", trigger.Id.ToString(), "deleted", ct: ct);
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
        var instanceNames = (await slotInstances.ReadAsync(ct)).ToList()
            .ToDictionary(i => i.Id, i => i.DisplayName);
        var healthById = (await triggerHealth.ReadAsync(ct)).ToList().ToDictionary(h => h.Id);

        return (await triggers.ReadAsync(ct)).ToList()
            .Select(t =>
            {
                var (health, failing) = Health(healthById.GetValueOrDefault(t.Id));
                var name = instanceNames.GetValueOrDefault(t.SlotInstanceId, "(deleted instance)");
                return new ConfiguredTrigger(
                    t.WorkflowConfigurationId, Descriptor.Kind,
                    $"Mailbox '{name}' · every {TimeText.Interval(t.PollIntervalSeconds)}",
                    name
                    + (t.SubjectContains.Length > 0 ? $" · subject ~ \"{t.SubjectContains}\"" : "")
                    + (t.FromContains.Length > 0 ? $" · from ~ \"{t.FromContains}\"" : ""),
                    health, failing);
            })
            .ToList();
    }

    /// <summary>How a mailbox trigger is doing, from its polling adapter's health sidecar.</summary>
    internal static (string Text, bool Failing) Health(TriggerHealthRecord? health)
    {
        if (health?.LastPollUtc is null)
            return ("not polled yet", false);
        if (health.LastError is { } error)
            return ($"failing since {TimeText.Relative(health.FailingSinceUtc ?? health.LastPollUtc.Value)} — {error}", true);

        var text = $"checked {TimeText.Relative(health.LastPollUtc.Value)}";
        if (health.LastDispatchUtc is { } dispatch)
            text += $" · last mail {TimeText.Relative(dispatch)}";
        return (text, false);
    }
}

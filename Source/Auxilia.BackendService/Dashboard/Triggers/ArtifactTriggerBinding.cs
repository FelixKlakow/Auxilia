using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;

namespace Auxilia.BackendService.Dashboard.Triggers;

/// <summary>Artifact-chain triggers: every persisted artifact of the type dispatches the configuration.</summary>
public sealed class ArtifactTriggerBinding(
    IDataAccess<ArtifactTriggerRecord> triggers,
    AuditLog auditLog) : ITriggerKindBinding
{
    public const string ArtifactTypeKey = "ArtifactType";

    public TriggerKindDescriptor Descriptor { get; } = new(
        TriggerDeclaration.Artifact, "Artifact chain",
        "Starts a run each time any workflow persists an artifact of the type.",
        [
            new SettingDescriptor(ArtifactTypeKey, "Artifact type", SettingKind.Text, Required: true,
                HelpText: "Each time any workflow persists an artifact of this type, this workflow runs.")
        ]);

    public IEnumerable<string> Validate(TriggerDraft trigger, TriggerValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(trigger.Settings.GetValueOrDefault(ArtifactTypeKey)))
            yield return "Artifact type is required for an artifact-chain trigger.";
    }

    public async Task<IReadOnlyList<TriggerDraft>> LoadAsync(Guid configurationId, CancellationToken ct)
        => (await triggers.ReadAsync(ct))
            .Where(t => t.WorkflowConfigurationId == configurationId).ToList()
            .Select(t =>
            {
                var draft = new TriggerDraft { ExistingId = t.Id, Kind = Descriptor.Kind };
                draft.Settings[ArtifactTypeKey] = t.ArtifactType;
                return draft;
            })
            .ToList();

    public async Task SaveAsync(string actor, Guid? actorPrincipalId, WorkflowConfigurationDraft draft,
        Guid configurationId, TriggerDraft trigger, CancellationToken ct)
    {
        var existing = trigger.ExistingId is { } id ? await triggers.ReadAsync(id, ct) : null;
        var record = new ArtifactTriggerRecord
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            ArtifactType = trigger.Settings[ArtifactTypeKey].Trim(),
            WorkflowType = draft.WorkflowType.Trim(),
            WorkflowPackageUri = draft.PackageUri.Trim(),
            Enabled = draft.Enabled,
            RunAsPrincipalId = actorPrincipalId ?? existing?.RunAsPrincipalId,
            WorkflowConfigurationId = configurationId
        };
        await triggers.SaveAsync(record, ct);
        await auditLog.AppendAsync(actor,
            existing is not null ? "trigger.artifact.updated" : "trigger.artifact.created",
            record.Id.ToString(), $"{record.ArtifactType} → {record.WorkflowType}", ct: ct);
    }

    public async Task RemoveExceptAsync(
        string actor, Guid configurationId, IReadOnlySet<Guid> keptIds, CancellationToken ct)
    {
        foreach (var trigger in (await triggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId && !keptIds.Contains(t.Id)).ToList())
        {
            if (await triggers.RemoveAsync(trigger.Id, ct))
                await auditLog.AppendAsync(actor, "trigger.artifact.deleted", trigger.Id.ToString(), "deleted", ct: ct);
        }
    }

    public async Task SetEnabledAsync(Guid configurationId, bool enabled, CancellationToken ct)
    {
        foreach (var trigger in (await triggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId).ToList())
            await triggers.SaveAsync(trigger with { Enabled = enabled }, ct);
    }

    public async Task<IReadOnlyList<ConfiguredTrigger>> ListConfiguredAsync(CancellationToken ct)
        => (await triggers.ReadAsync(ct)).ToList()
            .Where(t => t.WorkflowConfigurationId is not null)
            .Select(t => new ConfiguredTrigger(
                t.WorkflowConfigurationId!.Value, Descriptor.Kind,
                $"Artifact chain · after every '{t.ArtifactType}' artifact",
                $"after '{t.ArtifactType}'",
                null, false))
            .ToList();
}

using System.Text;
using System.Text.Json;
using Auxilia.Adapters.Email;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.Dashboard;

/// <summary>How a workflow configuration's runs are started.</summary>
public enum TriggerKind
{
    None,
    Schedule,
    ArtifactChain
}

/// <summary>One slot binding being edited; secret values are write-only (see <see cref="StoredSecretKeys"/>).</summary>
public sealed class SlotBindingDraft
{
    public string SlotName { get; set; } = "";
    public string ProviderType { get; set; } = "";
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Secret-kind keys that already have a stored value. Their inputs start empty;
    /// leaving them empty keeps the stored value on save — it is never read back.
    /// </summary>
    public HashSet<string> StoredSecretKeys { get; } = new(StringComparer.Ordinal);
}

/// <summary>Mutable editing model behind the visual workflow configuration editor (#20).</summary>
public sealed class WorkflowConfigurationDraft
{
    /// <summary>Natural-key name when editing an existing configuration; null when creating.</summary>
    public string? ExistingName { get; set; }

    public string DisplayName { get; set; } = "";
    public string WorkflowType { get; set; } = "";
    public string PackageUri { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<SlotBindingDraft> Bindings { get; } = [];

    public TriggerKind Trigger { get; set; } = TriggerKind.None;
    public int ScheduleIntervalSeconds { get; set; } = 3600;
    public string ScheduleContextJson { get; set; } = "";
    public string ArtifactType { get; set; } = "";
}

/// <summary>One configured workflow on the list page, joined with its triggers and latest run.</summary>
public sealed record WorkflowConfigurationOverview(
    Guid Id,
    string Name,
    string DisplayName,
    string WorkflowType,
    string PackageUri,
    bool Enabled,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<(string SlotName, string ProviderType)> Bindings,
    ScheduledTriggerRecord? Schedule,
    ArtifactTriggerRecord? ArtifactChain,
    WorkflowInstanceRecord? LastRun);

/// <summary>Known values offered as picker suggestions (free-text stays allowed).</summary>
public sealed record KnownWorkflowValues(
    IReadOnlyList<string> WorkflowTypes,
    IReadOnlyList<string> PackageUris,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SlotNamesByWorkflowType);

/// <summary>One slot a registered workflow declares in its schema.</summary>
public sealed record RegisteredWorkflowSlot(
    string SlotName, string? Description, string? Contract, bool Optional);

/// <summary>
/// One workflow package from the platform registry, joined with its stored schema. The
/// editor offers exactly these — package coordinates are never typed by hand.
/// </summary>
public sealed record RegisteredWorkflow(
    string WorkflowType,
    string PackageUri,
    string DisplayName,
    string Version,
    bool SchemaKnown,
    IReadOnlyList<RegisteredWorkflowSlot> Slots);

/// <summary>
/// Backing service of the visual workflow configuration editor (#20).
///
/// Read path: the shared platform data layer (the Steering Instance persists
/// <see cref="WorkflowConfigurationRecord"/>s there — same pattern as the slots page reading
/// provider records). Write path: the <c>slot-configurations</c> seeding exchange
/// (<see cref="UpsertWorkflowConfigurationCommand"/> / <see cref="RemoveWorkflowConfigurationCommand"/>)
/// so the Steering Instance stays the single writer of its configuration store.
/// Secret settings are write-only: drafts never carry stored secret values back to the UI,
/// and an empty secret input keeps the stored value. Every mutation is audited — without
/// settings values.
/// </summary>
public sealed class WorkflowConfigurationEditorService(
    IDataAccess<WorkflowConfigurationRecord> configurations,
    IDataAccess<WorkflowInstanceRecord> instances,
    IDataAccess<ScheduledTriggerRecord> scheduledTriggers,
    IDataAccess<ArtifactTriggerRecord> artifactTriggers,
    IDataAccess<SlotConfigurationRecord> slotConfigurations,
    IDataAccess<SlotProviderRecord> slotProviders,
    IDataAccess<WorkflowPackageRecord> workflowPackages,
    IDataAccess<WorkflowSchemaRecord> workflowSchemas,
    ProviderCatalogService catalog,
    ISettingsProtector protector,
    IMessageBusClient messageBus,
    AuditLog auditLog,
    IOptions<EmailTaskSourceSettings> emailSettings)
{
    internal const string SeedExchangeName = "slot-configurations";

    // ------------------------------------------------------------------ list

    public async Task<IReadOnlyList<WorkflowConfigurationOverview>> ListAsync(CancellationToken ct = default)
    {
        var records = (await configurations.ReadAsync(ct)).ToList();
        var schedules = (await scheduledTriggers.ReadAsync(ct)).ToList();
        var chains = (await artifactTriggers.ReadAsync(ct)).ToList();
        var runs = (await instances.ReadAsync(ct)).ToList();

        return records
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(record => new WorkflowConfigurationOverview(
                record.Id, record.Name, record.DisplayName, record.WorkflowType, record.PackageUri,
                record.Enabled, record.UpdatedUtc,
                ParseBindings(record.SlotBindingsJson)
                    .Select(b => (b.SlotName, b.ProviderType)).ToList(),
                schedules.FirstOrDefault(t => t.WorkflowConfigurationId == record.Id),
                chains.FirstOrDefault(t => t.WorkflowConfigurationId == record.Id),
                runs.Where(r => r.WorkflowConfigurationId == record.Id)
                    .OrderByDescending(r => r.CreatedUtc)
                    .FirstOrDefault()))
            .ToList();
    }

    // ------------------------------------------------------------------ draft

    /// <summary>Loads an existing configuration for editing; stored secret values are masked, never returned.</summary>
    public async Task<WorkflowConfigurationDraft?> LoadDraftAsync(Guid id, CancellationToken ct = default)
    {
        var record = await configurations.ReadAsync(id, ct);
        if (record is null)
            return null;

        var secretKeysByProvider = await SecretKeysByProviderAsync(ct);
        var draft = new WorkflowConfigurationDraft
        {
            ExistingName = record.Name,
            DisplayName = record.DisplayName,
            WorkflowType = record.WorkflowType,
            PackageUri = record.PackageUri,
            Enabled = record.Enabled
        };

        foreach (var binding in ParseBindings(record.SlotBindingsJson))
        {
            var bindingDraft = new SlotBindingDraft
            {
                SlotName = binding.SlotName,
                ProviderType = binding.ProviderType
            };
            var secretKeys = secretKeysByProvider.GetValueOrDefault(binding.ProviderType)
                             ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in UnprotectSettings(binding.ProtectedSettingsJson))
            {
                if (secretKeys.Contains(key))
                {
                    // Write-only: the input starts empty; an empty save keeps the stored value.
                    bindingDraft.Settings[key] = "";
                    if (!string.IsNullOrEmpty(value))
                        bindingDraft.StoredSecretKeys.Add(key);
                }
                else
                {
                    bindingDraft.Settings[key] = value;
                }
            }

            draft.Bindings.Add(bindingDraft);
        }

        var schedule = (await scheduledTriggers.ReadAsync(ct)).FirstOrDefault(t => t.WorkflowConfigurationId == id);
        var chain = (await artifactTriggers.ReadAsync(ct)).FirstOrDefault(t => t.WorkflowConfigurationId == id);
        if (schedule is not null)
        {
            draft.Trigger = TriggerKind.Schedule;
            draft.ScheduleIntervalSeconds = schedule.IntervalSeconds;
            draft.ScheduleContextJson = schedule.ContextJson ?? "";
        }
        else if (chain is not null)
        {
            draft.Trigger = TriggerKind.ArtifactChain;
            draft.ArtifactType = chain.ArtifactType;
        }

        return draft;
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// Validates the draft, merges kept secrets from the stored record, publishes the upsert
    /// over the bus, and wires the requested trigger records. Returns the configuration's
    /// natural-key name (generated from the display name on create).
    /// </summary>
    public async Task<string> SaveAsync(
        string actor, Guid? actorPrincipalId, WorkflowConfigurationDraft draft, CancellationToken ct = default)
    {
        var descriptorsByProvider = await DescriptorsByProviderAsync(ct);
        var schemaSlots = string.IsNullOrWhiteSpace(draft.WorkflowType)
            ? null
            : await SchemaSlotsOfAsync(draft.WorkflowType.Trim(), ct);
        Validate(draft, descriptorsByProvider, schemaSlots);

        var name = draft.ExistingName ?? await NewUniqueNameAsync(draft.DisplayName, ct);
        var existing = draft.ExistingName is null
            ? null
            : await configurations.ReadAsync(WorkflowConfigurationRecord.IdFor(draft.ExistingName), ct);

        var bindings = new List<SlotBindingSeed>();
        foreach (var binding in draft.Bindings)
        {
            var settings = EffectiveSettings(binding, existing, descriptorsByProvider);
            bindings.Add(new SlotBindingSeed(binding.SlotName.Trim(), binding.ProviderType, settings));
        }

        var command = new UpsertWorkflowConfigurationCommand(
            name, draft.DisplayName.Trim(), draft.WorkflowType.Trim(), draft.PackageUri.Trim(),
            draft.Enabled, bindings, actorPrincipalId);

        await messageBus.DeclareExchangeAsync(SeedExchangeName, ct);
        await messageBus.PublishToExchangeAsync(SeedExchangeName, command, ct);

        await ApplyTriggerWiringAsync(actor, actorPrincipalId, name, draft, ct);

        // Audited without settings values: only the binding topology is recorded.
        await auditLog.AppendAsync(actor, "workflow-configuration.saved", name,
            existing is null && draft.ExistingName is null ? "created" : "updated",
            JsonSerializer.Serialize(new
            {
                workflowType = command.WorkflowType,
                packageUri = command.PackageUri,
                enabled = command.Enabled,
                trigger = draft.Trigger.ToString(),
                slotBindings = bindings.Select(b => new { b.SlotName, b.ProviderType })
            }), ct);

        return name;
    }

    /// <summary>Enables or disables a configuration (and its wired trigger records) without touching its bindings.</summary>
    public async Task SetEnabledAsync(string actor, Guid id, bool enabled, CancellationToken ct = default)
    {
        var record = await configurations.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("Unknown workflow configuration.");

        var bindings = ParseBindings(record.SlotBindingsJson)
            .Select(b => new SlotBindingSeed(b.SlotName, b.ProviderType, UnprotectSettings(b.ProtectedSettingsJson)))
            .ToList();
        var command = new UpsertWorkflowConfigurationCommand(
            record.Name, record.DisplayName, record.WorkflowType, record.PackageUri,
            enabled, bindings, record.OwnerPrincipalId);
        await messageBus.DeclareExchangeAsync(SeedExchangeName, ct);
        await messageBus.PublishToExchangeAsync(SeedExchangeName, command, ct);

        foreach (var trigger in (await scheduledTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == id).ToList())
            await scheduledTriggers.SaveAsync(trigger with { Enabled = enabled }, ct);
        foreach (var trigger in (await artifactTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == id).ToList())
            await artifactTriggers.SaveAsync(trigger with { Enabled = enabled }, ct);

        await auditLog.AppendAsync(actor, "workflow-configuration.enabled-changed",
            record.Name, enabled ? "enabled" : "disabled", ct: ct);
    }

    /// <summary>Duplicates a stored configuration as a disabled "Copy of X"; triggers are not copied.</summary>
    public async Task<string> DuplicateAsync(string actor, Guid id, CancellationToken ct = default)
    {
        var record = await configurations.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("Unknown workflow configuration.");

        var displayName = $"Copy of {record.DisplayName}";
        var name = await NewUniqueNameAsync(displayName, ct);

        var bindings = ParseBindings(record.SlotBindingsJson)
            .Select(b => new SlotBindingSeed(b.SlotName, b.ProviderType, UnprotectSettings(b.ProtectedSettingsJson)))
            .ToList();

        var command = new UpsertWorkflowConfigurationCommand(
            name, displayName, record.WorkflowType, record.PackageUri,
            Enabled: false, bindings, record.OwnerPrincipalId);
        await messageBus.DeclareExchangeAsync(SeedExchangeName, ct);
        await messageBus.PublishToExchangeAsync(SeedExchangeName, command, ct);

        await auditLog.AppendAsync(actor, "workflow-configuration.duplicated", name,
            $"copy of '{record.Name}'", ct: ct);
        return name;
    }

    /// <summary>Publishes the removal over the bus and deletes the configuration's trigger records.</summary>
    public async Task DeleteAsync(string actor, Guid id, CancellationToken ct = default)
    {
        var record = await configurations.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("Unknown workflow configuration.");

        await messageBus.DeclareExchangeAsync(SeedExchangeName, ct);
        await messageBus.PublishToExchangeAsync(SeedExchangeName, new RemoveWorkflowConfigurationCommand(record.Name), ct);

        await RemoveTriggerRecordsAsync(actor, id, removeSchedule: true, removeChain: true, ct);

        await auditLog.AppendAsync(actor, "workflow-configuration.deleted", record.Name, "removed", ct: ct);
    }

    // ------------------------------------------------------------------ registry

    /// <summary>The workflow packages registered on this platform, joined with their stored schemas.</summary>
    public async Task<IReadOnlyList<RegisteredWorkflow>> RegisteredWorkflowsAsync(CancellationToken ct = default)
    {
        var packageRecords = (await workflowPackages.ReadAsync(ct)).ToList();
        var schemasByType = (await workflowSchemas.ReadAsync(ct)).ToList()
            .ToDictionary(r => r.WorkflowType, r => r.SchemaJson, StringComparer.Ordinal);

        return packageRecords
            .Select(p => ToRegisteredWorkflow(p, schemasByType.GetValueOrDefault(p.WorkflowType)))
            .OrderBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static RegisteredWorkflow ToRegisteredWorkflow(WorkflowPackageRecord package, string? schemaJson)
    {
        var schema = ParseSchema(schemaJson);
        return new RegisteredWorkflow(
            package.WorkflowType,
            package.PackageUri,
            package.DisplayName.Length > 0 ? package.DisplayName : package.WorkflowType,
            schema?.Version is { Length: > 0 } version ? version : package.Version,
            schema is not null,
            (schema?.Slots ?? [])
                .Select(s => new RegisteredWorkflowSlot(s.SlotName, s.Description, s.Contract, s.Optional))
                .ToList());
    }

    private static WorkflowSchema? ParseSchema(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize<WorkflowSchema>(schemaJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<RegisteredWorkflowSlot>?> SchemaSlotsOfAsync(
        string workflowType, CancellationToken ct)
    {
        var record = await workflowSchemas.ReadAsync(WorkflowSchemaRecord.IdFor(workflowType), ct);
        var schema = ParseSchema(record?.SchemaJson);
        return schema?.Slots
            .Select(s => new RegisteredWorkflowSlot(s.SlotName, s.Description, s.Contract, s.Optional))
            .ToList();
    }

    // ------------------------------------------------------------------ known values

    public async Task<KnownWorkflowValues> KnownValuesAsync(CancellationToken ct = default)
    {
        var records = (await configurations.ReadAsync(ct)).ToList();
        var schedules = (await scheduledTriggers.ReadAsync(ct)).ToList();
        var chains = (await artifactTriggers.ReadAsync(ct)).ToList();
        var slots = (await slotConfigurations.ReadAsync(ct)).ToList();
        var providers = (await slotProviders.ReadAsync(ct)).ToList();
        var email = emailSettings.Value;

        var libraryProviderTypes = providers
            .Where(p => !p.DllPath.EndsWith(".slothandler.dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.ProviderType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var workflowTypes = records.Select(r => r.WorkflowType)
            .Concat(schedules.Select(t => t.WorkflowType))
            .Concat(chains.Select(t => t.WorkflowType))
            .Concat(slots.Select(s => s.WorkflowType))
            .Concat(email.Enabled && !string.IsNullOrWhiteSpace(email.WorkflowType) ? [email.WorkflowType] : Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var packageUris = records.Select(r => r.PackageUri)
            .Concat(schedules.Select(t => t.WorkflowPackageUri))
            .Concat(chains.Select(t => t.WorkflowPackageUri))
            .Concat(email.Enabled && !string.IsNullOrWhiteSpace(email.WorkflowPackageUri) ? [email.WorkflowPackageUri] : Array.Empty<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();

        var slotNames = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var slotGroups = slots
            .Where(s => !libraryProviderTypes.Contains(s.ProviderType))
            .GroupBy(s => s.WorkflowType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(s => s.SlotName), StringComparer.Ordinal);
        foreach (var workflowType in workflowTypes)
        {
            var names = (slotGroups.GetValueOrDefault(workflowType) ?? [])
                .Concat(records.Where(r => r.WorkflowType == workflowType)
                    .SelectMany(r => ParseBindings(r.SlotBindingsJson).Select(b => b.SlotName)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            if (names.Count > 0)
                slotNames[workflowType] = names;
        }

        return new KnownWorkflowValues(workflowTypes, packageUris, slotNames);
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Merges the draft's settings over the stored ones: empty secret inputs keep the stored value.</summary>
    private Dictionary<string, string> EffectiveSettings(
        SlotBindingDraft binding,
        WorkflowConfigurationRecord? existing,
        IReadOnlyDictionary<string, IReadOnlyList<SettingDescriptor>> descriptorsByProvider)
    {
        var settings = binding.Settings
            .Where(s => !string.IsNullOrEmpty(s.Value))
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

        if (existing is null)
            return settings;

        var stored = ParseBindings(existing.SlotBindingsJson)
            .FirstOrDefault(b => b.SlotName == binding.SlotName.Trim() && b.ProviderType == binding.ProviderType);
        if (stored is null)
            return settings;

        var secretKeys = SecretKeysOf(descriptorsByProvider.GetValueOrDefault(binding.ProviderType));
        foreach (var (key, value) in UnprotectSettings(stored.ProtectedSettingsJson))
        {
            if (secretKeys.Contains(key) && !settings.ContainsKey(key) && !string.IsNullOrEmpty(value))
                settings[key] = value; // keep-unchanged: the user typed nothing new
        }

        return settings;
    }

    private void Validate(
        WorkflowConfigurationDraft draft,
        IReadOnlyDictionary<string, IReadOnlyList<SettingDescriptor>> descriptorsByProvider,
        IReadOnlyList<RegisteredWorkflowSlot>? schemaSlots)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
            errors.Add("Display name is required.");
        if (string.IsNullOrWhiteSpace(draft.WorkflowType) || string.IsNullOrWhiteSpace(draft.PackageUri))
            errors.Add("Pick the workflow this configuration runs.");

        // The stored schema knows which slots the workflow requires — every non-optional slot
        // must be bound before the configuration can dispatch successfully.
        foreach (var slot in schemaSlots ?? [])
        {
            if (!slot.Optional && draft.Bindings.All(b => b.SlotName.Trim() != slot.SlotName))
                errors.Add($"Slot '{slot.SlotName}' is required by this workflow.");
        }

        var slotNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in draft.Bindings)
        {
            var slot = binding.SlotName.Trim();
            if (slot.Length == 0)
            {
                errors.Add("Every slot needs a name.");
                continue;
            }
            if (!slotNames.Add(slot))
                errors.Add($"Slot '{slot}' is bound twice.");
            if (string.IsNullOrWhiteSpace(binding.ProviderType))
            {
                errors.Add($"Slot '{slot}' has no provider selected.");
                continue;
            }

            foreach (var descriptor in descriptorsByProvider.GetValueOrDefault(binding.ProviderType) ?? [])
            {
                if (!descriptor.Required)
                    continue;
                var hasValue = !string.IsNullOrWhiteSpace(binding.Settings.GetValueOrDefault(descriptor.Key));
                var keepsStoredSecret = descriptor.Kind == SettingKind.Secret
                                        && binding.StoredSecretKeys.Contains(descriptor.Key);
                if (!hasValue && !keepsStoredSecret)
                    errors.Add($"Slot '{slot}': '{descriptor.Label}' is required.");
            }
        }

        switch (draft.Trigger)
        {
            case TriggerKind.Schedule when draft.ScheduleIntervalSeconds < 1:
                errors.Add("Schedule interval must be at least 1 second.");
                break;
            case TriggerKind.Schedule when !string.IsNullOrWhiteSpace(draft.ScheduleContextJson):
                try
                {
                    if (JsonSerializer.Deserialize<Dictionary<string, string>>(draft.ScheduleContextJson) is null)
                        errors.Add("Schedule context must be a JSON object of string values.");
                }
                catch (JsonException)
                {
                    errors.Add("Schedule context must be a JSON object of string values.");
                }
                break;
            case TriggerKind.ArtifactChain when string.IsNullOrWhiteSpace(draft.ArtifactType):
                errors.Add("Artifact type is required for an artifact-chain trigger.");
                break;
        }

        if (errors.Count > 0)
            throw new ArgumentException(string.Join("\n", errors));
    }

    private async Task ApplyTriggerWiringAsync(
        string actor, Guid? actorPrincipalId, string name, WorkflowConfigurationDraft draft, CancellationToken ct)
    {
        var configurationId = WorkflowConfigurationRecord.IdFor(name);

        await RemoveTriggerRecordsAsync(actor, configurationId,
            removeSchedule: draft.Trigger != TriggerKind.Schedule,
            removeChain: draft.Trigger != TriggerKind.ArtifactChain, ct);

        switch (draft.Trigger)
        {
            case TriggerKind.Schedule:
            {
                var existing = (await scheduledTriggers.ReadAsync(ct))
                    .FirstOrDefault(t => t.WorkflowConfigurationId == configurationId);
                var record = new ScheduledTriggerRecord
                {
                    Id = existing?.Id ?? ScheduledTriggerRecord.IdForConfiguration(name),
                    WorkflowType = draft.WorkflowType.Trim(),
                    WorkflowPackageUri = draft.PackageUri.Trim(),
                    IntervalSeconds = draft.ScheduleIntervalSeconds,
                    ContextJson = string.IsNullOrWhiteSpace(draft.ScheduleContextJson)
                        ? null
                        : draft.ScheduleContextJson.Trim(),
                    Enabled = draft.Enabled,
                    RunAsPrincipalId = actorPrincipalId ?? existing?.RunAsPrincipalId,
                    LastDispatchedUtc = existing?.LastDispatchedUtc,
                    WorkflowConfigurationId = configurationId
                };
                var updated = await scheduledTriggers.SaveAsync(record, ct);
                await auditLog.AppendAsync(actor,
                    updated ? "trigger.scheduled.updated" : "trigger.scheduled.created",
                    record.Id.ToString(), record.WorkflowType, ct: ct);
                break;
            }
            case TriggerKind.ArtifactChain:
            {
                var existing = (await artifactTriggers.ReadAsync(ct))
                    .FirstOrDefault(t => t.WorkflowConfigurationId == configurationId);
                var record = new ArtifactTriggerRecord
                {
                    Id = existing?.Id ?? ArtifactTriggerRecord.IdForConfiguration(name),
                    ArtifactType = draft.ArtifactType.Trim(),
                    WorkflowType = draft.WorkflowType.Trim(),
                    WorkflowPackageUri = draft.PackageUri.Trim(),
                    Enabled = draft.Enabled,
                    RunAsPrincipalId = actorPrincipalId ?? existing?.RunAsPrincipalId,
                    WorkflowConfigurationId = configurationId
                };
                var updated = await artifactTriggers.SaveAsync(record, ct);
                await auditLog.AppendAsync(actor,
                    updated ? "trigger.artifact.updated" : "trigger.artifact.created",
                    record.Id.ToString(), $"{record.ArtifactType} → {record.WorkflowType}", ct: ct);
                break;
            }
        }
    }

    private async Task RemoveTriggerRecordsAsync(
        string actor, Guid configurationId, bool removeSchedule, bool removeChain, CancellationToken ct)
    {
        if (removeSchedule)
            foreach (var trigger in (await scheduledTriggers.ReadAsync(ct))
                         .Where(t => t.WorkflowConfigurationId == configurationId).ToList())
            {
                if (await scheduledTriggers.RemoveAsync(trigger.Id, ct))
                    await auditLog.AppendAsync(actor, "trigger.scheduled.deleted", trigger.Id.ToString(), "deleted", ct: ct);
            }

        if (removeChain)
            foreach (var trigger in (await artifactTriggers.ReadAsync(ct))
                         .Where(t => t.WorkflowConfigurationId == configurationId).ToList())
            {
                if (await artifactTriggers.RemoveAsync(trigger.Id, ct))
                    await auditLog.AppendAsync(actor, "trigger.artifact.deleted", trigger.Id.ToString(), "deleted", ct: ct);
            }
    }

    /// <summary>Derives a unique natural-key name from the display name ("Code Review" → "code-review").</summary>
    private async Task<string> NewUniqueNameAsync(string displayName, CancellationToken ct)
    {
        var slug = Slugify(displayName);
        var existing = (await configurations.ReadAsync(ct)).ToList()
            .Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (!existing.Contains(slug))
            return slug;
        for (var i = 2; ; i++)
        {
            var candidate = $"{slug}-{i}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    internal static string Slugify(string displayName)
    {
        var builder = new StringBuilder(displayName.Length);
        var lastWasDash = true; // suppress leading dashes
        foreach (var c in displayName.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().TrimEnd('-');
        return slug.Length == 0 ? "workflow-configuration" : slug;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<SettingDescriptor>>> DescriptorsByProviderAsync(
        CancellationToken ct)
        => (await catalog.ListAsync(ct)).ToDictionary(
            e => e.ProviderType, e => e.Descriptors, StringComparer.Ordinal);

    private async Task<IReadOnlyDictionary<string, HashSet<string>>> SecretKeysByProviderAsync(CancellationToken ct)
        => (await DescriptorsByProviderAsync(ct)).ToDictionary(
            e => e.Key, e => SecretKeysOf(e.Value), StringComparer.Ordinal);

    private static HashSet<string> SecretKeysOf(IReadOnlyList<SettingDescriptor>? descriptors)
        => (descriptors ?? [])
            .Where(d => d.Kind == SettingKind.Secret)
            .Select(d => d.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<WorkflowConfigurationSlotBinding> ParseBindings(string slotBindingsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<WorkflowConfigurationSlotBinding>>(slotBindingsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Dictionary<string, string> UnprotectSettings(string protectedSettingsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                protector.Unprotect(protectedSettingsJson)) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            // Wrong key or foreign format — edit starts from empty settings rather than crashing.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}

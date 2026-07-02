using System.Text;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.Dashboard;

/// <summary>The kinds of trigger a workflow configuration can wire (any number of each).</summary>
public enum TriggerKind
{
    Schedule,
    ArtifactChain,
    Mailbox
}

/// <summary>One trigger being edited; only the fields of its <see cref="Kind"/> apply.</summary>
public sealed class TriggerDraft
{
    /// <summary>The stored record this draft edits; null when newly added.</summary>
    public Guid? ExistingId { get; set; }

    public TriggerKind Kind { get; set; }

    // Schedule
    public int IntervalSeconds { get; set; } = 3600;
    public string ContextJson { get; set; } = "";

    // Artifact chain
    public string ArtifactType { get; set; } = "";

    // Mailbox
    /// <summary>The email slot instance whose mailbox is polled.</summary>
    public Guid? MailboxInstanceId { get; set; }
    public int PollIntervalSeconds { get; set; } = 15;
    /// <summary>Optional case-insensitive filters; a mail must match both to dispatch.</summary>
    public string SubjectContains { get; set; } = "";
    public string FromContains { get; set; } = "";
}

/// <summary>One slot binding being edited; secret values are write-only (see <see cref="StoredSecretKeys"/>).</summary>
public sealed class SlotBindingDraft
{
    public string SlotName { get; set; } = "";
    public string ProviderType { get; set; } = "";

    /// <summary>
    /// When set, the binding uses a reusable slot instance — provider and settings come from
    /// the instance and the inline values below are ignored.
    /// </summary>
    public Guid? SlotInstanceId { get; set; }

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

    /// <summary>All triggers of this configuration — a run starts when ANY of them fires.</summary>
    public List<TriggerDraft> Triggers { get; } = [];
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
    IReadOnlyList<TriggerSummary> TriggerSummaries,
    WorkflowInstanceRecord? LastRun);

/// <summary>One trigger of a configuration with its live health: what it is, and how it is doing.</summary>
public sealed record TriggerSummary(string Label, string? Health, bool Failing);

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
    IReadOnlyList<RegisteredWorkflowSlot> Slots,
    IReadOnlyList<TriggerDeclaration> DeclaredTriggers,
    IReadOnlyList<string> ConsumedArtifacts);

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
    IDataAccess<SlotInstanceRecord> slotInstances,
    IDataAccess<MailboxTriggerRecord> mailboxTriggers,
    IDataAccess<TriggerHealthRecord> triggerHealth,
    ProviderCatalogService catalog,
    ISettingsProtector protector,
    IMessageBusClient messageBus,
    AuditLog auditLog,
    TimeProvider timeProvider)
{
    internal const string SeedExchangeName = "slot-configurations";

    // ------------------------------------------------------------------ list

    public async Task<IReadOnlyList<WorkflowConfigurationOverview>> ListAsync(CancellationToken ct = default)
    {
        var records = (await configurations.ReadAsync(ct)).ToList();
        var schedules = (await scheduledTriggers.ReadAsync(ct)).ToList();
        var chains = (await artifactTriggers.ReadAsync(ct)).ToList();
        var mailboxes = (await mailboxTriggers.ReadAsync(ct)).ToList();
        var instanceNames = (await slotInstances.ReadAsync(ct)).ToList()
            .ToDictionary(i => i.Id, i => i.DisplayName);
        var healthById = (await triggerHealth.ReadAsync(ct)).ToList().ToDictionary(h => h.Id);
        var runs = (await instances.ReadAsync(ct)).ToList();
        var now = timeProvider.GetUtcNow();

        return records
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(record => new WorkflowConfigurationOverview(
                record.Id, record.Name, record.DisplayName, record.WorkflowType, record.PackageUri,
                record.Enabled, record.UpdatedUtc,
                ParseBindings(record.SlotBindingsJson)
                    .Select(b => (b.SlotName, b.ProviderType)).ToList(),
                TriggerSummariesOf(record.Id, schedules, chains, mailboxes, instanceNames, healthById, now),
                runs.Where(r => r.WorkflowConfigurationId == record.Id)
                    .OrderByDescending(r => r.CreatedUtc)
                    .FirstOrDefault()))
            .ToList();
    }

    private static IReadOnlyList<TriggerSummary> TriggerSummariesOf(
        Guid configurationId,
        IReadOnlyList<ScheduledTriggerRecord> schedules,
        IReadOnlyList<ArtifactTriggerRecord> chains,
        IReadOnlyList<MailboxTriggerRecord> mailboxes,
        IReadOnlyDictionary<Guid, string> instanceNames,
        IReadOnlyDictionary<Guid, TriggerHealthRecord> healthById,
        DateTimeOffset now)
    {
        var summaries = new List<TriggerSummary>();
        summaries.AddRange(schedules
            .Where(t => t.WorkflowConfigurationId == configurationId)
            .Select(t =>
            {
                var (health, failing) = ScheduleHealth(t, now);
                return new TriggerSummary($"Schedule · every {TimeText.Interval(t.IntervalSeconds)}", health, failing);
            }));
        summaries.AddRange(chains
            .Where(t => t.WorkflowConfigurationId == configurationId)
            .Select(t => new TriggerSummary(
                $"Artifact chain · after every '{t.ArtifactType}' artifact", null, false)));
        summaries.AddRange(mailboxes
            .Where(t => t.WorkflowConfigurationId == configurationId)
            .Select(t =>
            {
                var (health, failing) = MailboxHealth(healthById.GetValueOrDefault(t.Id));
                return new TriggerSummary(
                    $"Mailbox '{instanceNames.GetValueOrDefault(t.SlotInstanceId, "(deleted instance)")}' · every {TimeText.Interval(t.PollIntervalSeconds)}",
                    health, failing);
            }));
        return summaries;
    }

    /// <summary>How a mailbox trigger is doing, from its polling adapter's health sidecar.</summary>
    internal static (string Text, bool Failing) MailboxHealth(TriggerHealthRecord? health)
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

    /// <summary>When a schedule fires next, from its dispatch bookkeeping.</summary>
    internal static (string Text, bool Failing) ScheduleHealth(ScheduledTriggerRecord schedule, DateTimeOffset now)
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
                ProviderType = binding.SlotInstanceId is null ? binding.ProviderType : "",
                SlotInstanceId = binding.SlotInstanceId
            };
            if (binding.SlotInstanceId is not null)
            {
                // Instance-backed bindings carry no settings of their own.
                draft.Bindings.Add(bindingDraft);
                continue;
            }
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

        foreach (var schedule in (await scheduledTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == id).ToList())
            draft.Triggers.Add(new TriggerDraft
            {
                ExistingId = schedule.Id,
                Kind = TriggerKind.Schedule,
                IntervalSeconds = schedule.IntervalSeconds,
                ContextJson = schedule.ContextJson ?? ""
            });
        foreach (var chain in (await artifactTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == id).ToList())
            draft.Triggers.Add(new TriggerDraft
            {
                ExistingId = chain.Id,
                Kind = TriggerKind.ArtifactChain,
                ArtifactType = chain.ArtifactType
            });
        foreach (var mailbox in (await mailboxTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == id).ToList())
            draft.Triggers.Add(new TriggerDraft
            {
                ExistingId = mailbox.Id,
                Kind = TriggerKind.Mailbox,
                MailboxInstanceId = mailbox.SlotInstanceId,
                PollIntervalSeconds = mailbox.PollIntervalSeconds,
                SubjectContains = mailbox.SubjectContains,
                FromContains = mailbox.FromContains
            });

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
        var instancesById = (await slotInstances.ReadAsync(ct)).ToList()
            .ToDictionary(r => r.Id);
        Validate(draft, descriptorsByProvider, schemaSlots, instancesById, actorPrincipalId);

        var name = draft.ExistingName ?? await NewUniqueNameAsync(draft.DisplayName, ct);
        var existing = draft.ExistingName is null
            ? null
            : await configurations.ReadAsync(WorkflowConfigurationRecord.IdFor(draft.ExistingName), ct);

        var bindings = new List<SlotBindingSeed>();
        foreach (var binding in draft.Bindings)
        {
            if (binding.SlotInstanceId is { } instanceId)
            {
                bindings.Add(new SlotBindingSeed(
                    binding.SlotName.Trim(), "", new Dictionary<string, string>(), instanceId));
                continue;
            }
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
                triggers = draft.Triggers.Select(t => t.Kind.ToString()),
                slotBindings = bindings.Select(b => new { b.SlotName, b.ProviderType })
            }), ct);

        return name;
    }

    /// <summary>Enables or disables a configuration (and its wired trigger records) without touching its bindings.</summary>
    public async Task SetEnabledAsync(string actor, Guid id, bool enabled, CancellationToken ct = default)
    {
        var record = await configurations.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("Unknown workflow configuration.");

        var bindings = ParseBindings(record.SlotBindingsJson).Select(ToSeed).ToList();
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
        foreach (var trigger in (await mailboxTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == id).ToList())
            await mailboxTriggers.SaveAsync(trigger with { Enabled = enabled }, ct);

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

        var bindings = ParseBindings(record.SlotBindingsJson).Select(ToSeed).ToList();

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

        await RemoveTriggerRecordsAsync(actor, id, ct);

        await auditLog.AppendAsync(actor, "workflow-configuration.deleted", record.Name, "removed", ct: ct);
    }

    // ------------------------------------------------------------------ flow view

    /// <summary>One node graph of the flow view: triggers → workflow → outputs → consumers.</summary>
    public sealed record WorkflowFlow(
        WorkflowConfigurationOverview Configuration,
        IReadOnlyList<FlowTrigger> Triggers,
        IReadOnlyList<FlowOutput> Outputs,
        IReadOnlyList<FlowPaletteEntry> Palette);

    public sealed record FlowTrigger(
        TriggerKind Kind, string Label, string? Health = null, bool Failing = false);

    /// <summary>A declared output, the configurations chained to run after it, and the chainable candidates.</summary>
    public sealed record FlowOutput(
        string Name, string? Description,
        IReadOnlyList<FlowConsumer> Consumers,
        IReadOnlyList<FlowChainCandidate> Candidates);

    public sealed record FlowConsumer(Guid ConfigurationId, string DisplayName, Guid TriggerId);

    /// <summary>
    /// A configuration that can be chained onto an output. <see cref="MatchesCriteria"/> is true
    /// when its workflow declares it consumes the output's artifact type; workflows declaring no
    /// consumed artifacts are unconstrained and offered too (but rank behind declared matches).
    /// </summary>
    public sealed record FlowChainCandidate(
        Guid ConfigurationId, string DisplayName, bool MatchesCriteria);

    /// <summary>
    /// One entry of the flow view's palette: every other configuration, with how its workflow's
    /// declared consumption criteria relate to this workflow's outputs.
    /// </summary>
    public sealed record FlowPaletteEntry(
        Guid ConfigurationId, string DisplayName, string WorkflowType, FlowPaletteMatch Match);

    public enum FlowPaletteMatch
    {
        /// <summary>Declares it consumes at least one of this workflow's output types.</summary>
        DeclaredMatch,

        /// <summary>Declares no consumed artifact types — chainable onto any output.</summary>
        Unconstrained,

        /// <summary>Declares consumed artifact types, none of which this workflow produces.</summary>
        Incompatible
    }

    /// <summary>Every artifact type any registered workflow declares as an output — the chaining vocabulary.</summary>
    public async Task<IReadOnlyList<string>> KnownArtifactTypesAsync(CancellationToken ct = default)
        => (await workflowSchemas.ReadAsync(ct)).ToList()
            .SelectMany(record => ParseSchema(record.SchemaJson)?.Outputs ?? [])
            .Select(output => output.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Assembles the flow graph of one configuration from triggers, schema outputs, and chaining records.</summary>
    public async Task<WorkflowFlow?> FlowAsync(Guid id, CancellationToken ct = default)
    {
        var overviews = await ListAsync(ct);
        var configuration = overviews.FirstOrDefault(o => o.Id == id);
        if (configuration is null)
            return null;

        var instanceNames = (await slotInstances.ReadAsync(ct)).ToList()
            .ToDictionary(i => i.Id, i => i.DisplayName);
        var healthById = (await triggerHealth.ReadAsync(ct)).ToList().ToDictionary(h => h.Id);
        var now = timeProvider.GetUtcNow();
        var triggers = new List<FlowTrigger>();
        triggers.AddRange((await scheduledTriggers.ReadAsync(ct)).ToList()
            .Where(t => t.WorkflowConfigurationId == id)
            .Select(t =>
            {
                var (health, failing) = ScheduleHealth(t, now);
                return new FlowTrigger(TriggerKind.Schedule,
                    $"every {TimeText.Interval(t.IntervalSeconds)}", health, failing);
            }));
        triggers.AddRange((await mailboxTriggers.ReadAsync(ct)).ToList()
            .Where(t => t.WorkflowConfigurationId == id)
            .Select(t =>
            {
                var (health, failing) = MailboxHealth(healthById.GetValueOrDefault(t.Id));
                return new FlowTrigger(TriggerKind.Mailbox,
                    instanceNames.GetValueOrDefault(t.SlotInstanceId, "(deleted instance)")
                    + (t.SubjectContains.Length > 0 ? $" · subject ~ \"{t.SubjectContains}\"" : "")
                    + (t.FromContains.Length > 0 ? $" · from ~ \"{t.FromContains}\"" : ""),
                    health, failing);
            }));
        var chains = (await artifactTriggers.ReadAsync(ct)).ToList();
        triggers.AddRange(chains
            .Where(t => t.WorkflowConfigurationId == id)
            .Select(t => new FlowTrigger(TriggerKind.ArtifactChain, $"after '{t.ArtifactType}'")));

        // Persisted artifacts carry the declared output's name as their artifact type — a
        // chaining record on that type makes its configuration a consumer of the output.
        var schema = ParseSchema(
            (await workflowSchemas.ReadAsync(WorkflowSchemaRecord.IdFor(configuration.WorkflowType), ct))?.SchemaJson);
        var overviewsById = overviews.ToDictionary(o => o.Id);

        // Chaining criteria: what each candidate's workflow declares it can consume.
        var consumedByType = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var record in (await workflowSchemas.ReadAsync(ct)).ToList())
            consumedByType[record.WorkflowType] = ParseSchema(record.SchemaJson)?.ConsumedArtifacts ?? [];

        var outputs = (schema?.Outputs ?? [])
            .Select(output => new FlowOutput(
                output.Name, output.Description,
                chains
                    .Where(t => t.ArtifactType == output.Name && t.WorkflowConfigurationId is not null
                                && overviewsById.ContainsKey(t.WorkflowConfigurationId.Value))
                    .Select(t => new FlowConsumer(
                        t.WorkflowConfigurationId!.Value,
                        overviewsById[t.WorkflowConfigurationId.Value].DisplayName,
                        t.Id))
                    .ToList(),
                overviews
                    .Where(o => o.Id != id)
                    .Select(o => new FlowChainCandidate(o.Id, o.DisplayName,
                        (consumedByType.GetValueOrDefault(o.WorkflowType) ?? [])
                        .Contains(output.Name, StringComparer.Ordinal)))
                    // Declared consumption is the criteria; declaring nothing = unconstrained.
                    .Where(c => c.MatchesCriteria
                                || (consumedByType.GetValueOrDefault(
                                        overviewsById[c.ConfigurationId].WorkflowType) ?? []).Count == 0)
                    .OrderByDescending(c => c.MatchesCriteria)
                    .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();

        var outputNames = outputs.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var palette = overviews
            .Where(o => o.Id != id)
            .Select(o =>
            {
                var consumed = consumedByType.GetValueOrDefault(o.WorkflowType) ?? [];
                var match = consumed.Count == 0
                    ? FlowPaletteMatch.Unconstrained
                    : consumed.Any(outputNames.Contains)
                        ? FlowPaletteMatch.DeclaredMatch
                        : FlowPaletteMatch.Incompatible;
                return new FlowPaletteEntry(o.Id, o.DisplayName, o.WorkflowType, match);
            })
            .OrderBy(e => e.Match)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkflowFlow(configuration, triggers, outputs, palette);
    }

    /// <summary>
    /// Chains <paramref name="targetConfigurationId"/> after <paramref name="outputName"/> of the
    /// source configuration: every persisted artifact of that type dispatches the target.
    /// </summary>
    public async Task ChainAsync(
        string actor, Guid? actorPrincipalId, Guid sourceConfigurationId, string outputName,
        Guid targetConfigurationId, CancellationToken ct = default)
    {
        var source = await configurations.ReadAsync(sourceConfigurationId, ct)
                     ?? throw new InvalidOperationException("Unknown source workflow configuration.");
        var target = await configurations.ReadAsync(targetConfigurationId, ct)
                     ?? throw new InvalidOperationException("Unknown target workflow configuration.");
        if (string.IsNullOrWhiteSpace(outputName))
            throw new ArgumentException("Output name is required.", nameof(outputName));

        var record = new ArtifactTriggerRecord
        {
            Id = Guid.NewGuid(),
            ArtifactType = outputName.Trim(),
            WorkflowType = target.WorkflowType,
            WorkflowPackageUri = target.PackageUri,
            Enabled = target.Enabled,
            RunAsPrincipalId = actorPrincipalId,
            WorkflowConfigurationId = target.Id
        };
        await artifactTriggers.SaveAsync(record, ct);
        await auditLog.AppendAsync(actor, "trigger.artifact.created", record.Id.ToString(),
            $"{source.Name}:{record.ArtifactType} → {target.Name}", ct: ct);
    }

    /// <summary>Removes a chaining record created via the flow view.</summary>
    public async Task UnchainAsync(string actor, Guid triggerId, CancellationToken ct = default)
    {
        if (await artifactTriggers.RemoveAsync(triggerId, ct))
            await auditLog.AppendAsync(actor, "trigger.artifact.deleted", triggerId.ToString(), "deleted", ct: ct);
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
                .ToList(),
            schema?.Triggers ?? [],
            schema?.ConsumedArtifacts ?? []);
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

        var libraryProviderTypes = providers
            .Where(p => !p.DllPath.EndsWith(".slothandler.dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.ProviderType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var workflowTypes = records.Select(r => r.WorkflowType)
            .Concat(schedules.Select(t => t.WorkflowType))
            .Concat(chains.Select(t => t.WorkflowType))
            .Concat(slots.Select(s => s.WorkflowType))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var packageUris = records.Select(r => r.PackageUri)
            .Concat(schedules.Select(t => t.WorkflowPackageUri))
            .Concat(chains.Select(t => t.WorkflowPackageUri))
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
        IReadOnlyList<RegisteredWorkflowSlot>? schemaSlots,
        IReadOnlyDictionary<Guid, SlotInstanceRecord> instancesById,
        Guid? actorPrincipalId)
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
            if (binding.SlotInstanceId is { } instanceId)
            {
                // Instance-backed binding: existence and access instead of inline settings.
                if (!instancesById.TryGetValue(instanceId, out var instance))
                    errors.Add($"Slot '{slot}': the selected slot instance no longer exists.");
                else if (actorPrincipalId is { } actor && !InstanceAccessible(instance, actor))
                    errors.Add($"Slot '{slot}': you have no access to the selected slot instance.");
                continue;
            }
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

        foreach (var trigger in draft.Triggers)
        {
            switch (trigger.Kind)
            {
                case TriggerKind.Schedule when trigger.IntervalSeconds < 1:
                    errors.Add("Schedule interval must be at least 1 second.");
                    break;
                case TriggerKind.Schedule when !string.IsNullOrWhiteSpace(trigger.ContextJson):
                    try
                    {
                        if (JsonSerializer.Deserialize<Dictionary<string, string>>(trigger.ContextJson) is null)
                            errors.Add("Schedule context must be a JSON object of string values.");
                    }
                    catch (JsonException)
                    {
                        errors.Add("Schedule context must be a JSON object of string values.");
                    }
                    break;
                case TriggerKind.ArtifactChain when string.IsNullOrWhiteSpace(trigger.ArtifactType):
                    errors.Add("Artifact type is required for an artifact-chain trigger.");
                    break;
                case TriggerKind.Mailbox when trigger.MailboxInstanceId is null:
                    errors.Add("Pick the mailbox slot instance the mailbox trigger polls.");
                    break;
                case TriggerKind.Mailbox when !instancesById.ContainsKey(trigger.MailboxInstanceId.Value):
                    errors.Add("The mailbox trigger's slot instance no longer exists.");
                    break;
                case TriggerKind.Mailbox when trigger.PollIntervalSeconds < 1:
                    errors.Add("Mailbox poll interval must be at least 1 second.");
                    break;
                case TriggerKind.Mailbox when actorPrincipalId is { } actor
                                              && !InstanceAccessible(instancesById[trigger.MailboxInstanceId.Value], actor):
                    errors.Add("You have no access to the mailbox trigger's slot instance.");
                    break;
            }
        }

        if (errors.Count > 0)
            throw new ArgumentException(string.Join("\n", errors));
    }

    /// <summary>Round-trips a stored binding into a seed: instance references stay references.</summary>
    private SlotBindingSeed ToSeed(WorkflowConfigurationSlotBinding binding)
        => binding.SlotInstanceId is { } instanceId
            ? new SlotBindingSeed(binding.SlotName, "", new Dictionary<string, string>(), instanceId)
            : new SlotBindingSeed(binding.SlotName, binding.ProviderType, UnprotectSettings(binding.ProtectedSettingsJson));

    private static bool InstanceAccessible(SlotInstanceRecord instance, Guid principalId)
        => instance.Scope == SlotInstanceScope.Company
           || instance.OwnerPrincipalId == principalId
           || (JsonSerializer.Deserialize<List<Guid>>(instance.AssignedPrincipalIdsJson) ?? [])
               .Contains(principalId);

    /// <summary>
    /// Reconciles the draft's trigger list against the stored records: kept drafts update
    /// their record in place (a schedule keeps its <c>LastDispatchedUtc</c> so editing never
    /// causes a surprise dispatch), new drafts create records, and records without a draft
    /// are deleted. Every change is audited.
    /// </summary>
    private async Task ApplyTriggerWiringAsync(
        string actor, Guid? actorPrincipalId, string name, WorkflowConfigurationDraft draft, CancellationToken ct)
    {
        var configurationId = WorkflowConfigurationRecord.IdFor(name);
        var keptIds = draft.Triggers
            .Where(t => t.ExistingId is not null)
            .Select(t => t.ExistingId!.Value)
            .ToHashSet();

        // Deletions first so a removed trigger cannot fire while its replacement is written.
        foreach (var trigger in (await scheduledTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId && !keptIds.Contains(t.Id)).ToList())
        {
            if (await scheduledTriggers.RemoveAsync(trigger.Id, ct))
                await auditLog.AppendAsync(actor, "trigger.scheduled.deleted", trigger.Id.ToString(), "deleted", ct: ct);
        }
        foreach (var trigger in (await artifactTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId && !keptIds.Contains(t.Id)).ToList())
        {
            if (await artifactTriggers.RemoveAsync(trigger.Id, ct))
                await auditLog.AppendAsync(actor, "trigger.artifact.deleted", trigger.Id.ToString(), "deleted", ct: ct);
        }
        foreach (var trigger in (await mailboxTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId && !keptIds.Contains(t.Id)).ToList())
        {
            if (await mailboxTriggers.RemoveAsync(trigger.Id, ct))
                await auditLog.AppendAsync(actor, "trigger.mailbox.deleted", trigger.Id.ToString(), "deleted", ct: ct);
        }

        foreach (var trigger in draft.Triggers)
        {
            switch (trigger.Kind)
            {
                case TriggerKind.Schedule:
                {
                    var existing = trigger.ExistingId is { } id
                        ? await scheduledTriggers.ReadAsync(id, ct)
                        : null;
                    var record = new ScheduledTriggerRecord
                    {
                        Id = existing?.Id ?? Guid.NewGuid(),
                        WorkflowType = draft.WorkflowType.Trim(),
                        WorkflowPackageUri = draft.PackageUri.Trim(),
                        IntervalSeconds = trigger.IntervalSeconds,
                        ContextJson = string.IsNullOrWhiteSpace(trigger.ContextJson)
                            ? null
                            : trigger.ContextJson.Trim(),
                        Enabled = draft.Enabled,
                        RunAsPrincipalId = actorPrincipalId ?? existing?.RunAsPrincipalId,
                        LastDispatchedUtc = existing?.LastDispatchedUtc,
                        WorkflowConfigurationId = configurationId
                    };
                    await scheduledTriggers.SaveAsync(record, ct);
                    await auditLog.AppendAsync(actor,
                        existing is not null ? "trigger.scheduled.updated" : "trigger.scheduled.created",
                        record.Id.ToString(), record.WorkflowType, ct: ct);
                    break;
                }
                case TriggerKind.ArtifactChain:
                {
                    var existing = trigger.ExistingId is { } id
                        ? await artifactTriggers.ReadAsync(id, ct)
                        : null;
                    var record = new ArtifactTriggerRecord
                    {
                        Id = existing?.Id ?? Guid.NewGuid(),
                        ArtifactType = trigger.ArtifactType.Trim(),
                        WorkflowType = draft.WorkflowType.Trim(),
                        WorkflowPackageUri = draft.PackageUri.Trim(),
                        Enabled = draft.Enabled,
                        RunAsPrincipalId = actorPrincipalId ?? existing?.RunAsPrincipalId,
                        WorkflowConfigurationId = configurationId
                    };
                    await artifactTriggers.SaveAsync(record, ct);
                    await auditLog.AppendAsync(actor,
                        existing is not null ? "trigger.artifact.updated" : "trigger.artifact.created",
                        record.Id.ToString(), $"{record.ArtifactType} → {record.WorkflowType}", ct: ct);
                    break;
                }
                case TriggerKind.Mailbox:
                {
                    var existing = trigger.ExistingId is { } id
                        ? await mailboxTriggers.ReadAsync(id, ct)
                        : null;
                    var record = new MailboxTriggerRecord
                    {
                        Id = existing?.Id ?? Guid.NewGuid(),
                        WorkflowConfigurationId = configurationId,
                        SlotInstanceId = trigger.MailboxInstanceId!.Value,
                        PollIntervalSeconds = trigger.PollIntervalSeconds,
                        Enabled = draft.Enabled,
                        RunAsPrincipalId = actorPrincipalId ?? existing?.RunAsPrincipalId,
                        SubjectContains = trigger.SubjectContains.Trim(),
                        FromContains = trigger.FromContains.Trim()
                    };
                    await mailboxTriggers.SaveAsync(record, ct);
                    await auditLog.AppendAsync(actor,
                        existing is not null ? "trigger.mailbox.updated" : "trigger.mailbox.created",
                        record.Id.ToString(), record.SlotInstanceId.ToString(), ct: ct);
                    break;
                }
            }
        }
    }

    private async Task RemoveTriggerRecordsAsync(string actor, Guid configurationId, CancellationToken ct)
    {
        foreach (var trigger in (await scheduledTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId).ToList())
        {
            if (await scheduledTriggers.RemoveAsync(trigger.Id, ct))
                await auditLog.AppendAsync(actor, "trigger.scheduled.deleted", trigger.Id.ToString(), "deleted", ct: ct);
        }

        foreach (var trigger in (await artifactTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId).ToList())
        {
            if (await artifactTriggers.RemoveAsync(trigger.Id, ct))
                await auditLog.AppendAsync(actor, "trigger.artifact.deleted", trigger.Id.ToString(), "deleted", ct: ct);
        }

        foreach (var trigger in (await mailboxTriggers.ReadAsync(ct))
                     .Where(t => t.WorkflowConfigurationId == configurationId).ToList())
        {
            if (await mailboxTriggers.RemoveAsync(trigger.Id, ct))
                await auditLog.AppendAsync(actor, "trigger.mailbox.deleted", trigger.Id.ToString(), "deleted", ct: ct);
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

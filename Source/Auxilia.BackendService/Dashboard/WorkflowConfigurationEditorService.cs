using System.Text;
using System.Text.Json;
using Auxilia.BackendService.Dashboard.Triggers;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.Dashboard;

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
    WorkflowInstanceRecord? LastRun)
{
    /// <summary>What a manual run of this workflow needs — drives the run form generically.</summary>
    public IReadOnlyList<WorkflowInputDescriptor> Inputs { get; init; } = [];
}

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

/// <summary>One workflow slot a provider can back (contract match) — what an instance of it is FOR.</summary>
public sealed record ProviderSlotTarget(string SlotName, string WorkflowDisplayName);

/// <summary>
/// One workflow package from the platform registry, joined with its stored schema and its
/// admin curation. The editor offers exactly these — package coordinates are never typed by
/// hand — and only the <see cref="Enabled"/> ones can be newly configured or run.
/// </summary>
public sealed record RegisteredWorkflow(
    string WorkflowType,
    string PackageUri,
    string DisplayName,
    string Version,
    bool SchemaKnown,
    IReadOnlyList<RegisteredWorkflowSlot> Slots,
    IReadOnlyList<TriggerDeclaration> DeclaredTriggers,
    IReadOnlyList<string> ConsumedArtifacts,
    bool Enabled = true);

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
    IDataAccess<WorkflowCatalogRecord> workflowCatalog,
    TriggerKindCatalog triggerKinds,
    ProviderCatalogService catalog,
    ISettingsProtector protector,
    IMessageBusClient messageBus,
    AuditLog auditLog,
    IPolicyEngine policyEngine,
    Microsoft.Extensions.Options.IOptions<PlatformHost.PlatformHostSettings> platformHostSettings)
{
    internal const string SeedExchangeName = "slot-configurations";

    /// <summary>The trigger kinds this platform knows — the editor renders them generically.</summary>
    public TriggerKindCatalog TriggerKinds => triggerKinds;

    // ------------------------------------------------------------------ list

    public async Task<IReadOnlyList<WorkflowConfigurationOverview>> ListAsync(CancellationToken ct = default)
    {
        var records = (await configurations.ReadAsync(ct)).ToList();
        var triggersByConfiguration = (await ConfiguredTriggersAsync(ct)).ToLookup(t => t.ConfigurationId);
        var runs = (await instances.ReadAsync(ct)).ToList();
        var schemasByType = (await workflowSchemas.ReadAsync(ct)).ToList()
            .ToDictionary(r => r.WorkflowType, r => ParseSchema(r.SchemaJson), StringComparer.Ordinal);

        return records
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(record => new WorkflowConfigurationOverview(
                record.Id, record.Name, record.DisplayName, record.WorkflowType, record.PackageUri,
                record.Enabled, record.UpdatedUtc,
                ParseBindings(record.SlotBindingsJson)
                    .Select(b => (b.SlotName, b.ProviderType)).ToList(),
                triggersByConfiguration[record.Id]
                    .Select(t => new TriggerSummary(t.ListLabel, t.Health, t.Failing)).ToList(),
                runs.Where(r => r.WorkflowConfigurationId == record.Id)
                    .OrderByDescending(r => r.CreatedUtc)
                    .FirstOrDefault())
            {
                Inputs = DeclaredInputsOf(schemasByType.GetValueOrDefault(record.WorkflowType))
            })
            .ToList();
    }

    /// <summary>Every wired trigger of every configuration, in trigger-kind registration order.</summary>
    private async Task<IReadOnlyList<ConfiguredTrigger>> ConfiguredTriggersAsync(CancellationToken ct)
    {
        var configured = new List<ConfiguredTrigger>();
        foreach (var binding in triggerKinds.Bindings)
            configured.AddRange(await binding.ListConfiguredAsync(ct));
        return configured;
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

        foreach (var binding in triggerKinds.Bindings)
            draft.Triggers.AddRange(await binding.LoadAsync(id, ct));

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
        // Admin-disabled settings are neither offered nor required (stored secrets still merge).
        var enabledDescriptorsByProvider = (await catalog.ListAsync(ct)).ToDictionary(
            e => e.ProviderType, e => e.EnabledDescriptors, StringComparer.Ordinal);
        var schema = string.IsNullOrWhiteSpace(draft.WorkflowType)
            ? null
            : ParseSchema((await workflowSchemas.ReadAsync(
                WorkflowSchemaRecord.IdFor(draft.WorkflowType.Trim()), ct))?.SchemaJson);
        var schemaSlots = schema?.Slots
            .Select(s => new RegisteredWorkflowSlot(s.SlotName, s.Description, s.Contract, s.Optional))
            .ToList();
        var instancesById = (await slotInstances.ReadAsync(ct)).ToList()
            .ToDictionary(r => r.Id);
        Validate(draft, enabledDescriptorsByProvider, schemaSlots,
            schema?.Triggers.Select(t => t.Kind).ToList(), instancesById, actorPrincipalId);

        // Existing configurations of a disabled type stay editable; new ones are refused.
        if (draft.ExistingName is null && draft.WorkflowType.Trim() is { Length: > 0 } newType
            && !await WorkflowTypeEnabledAsync(newType, ct))
            throw new ArgumentException(
                $"An administrator disabled the workflow '{newType}' — it cannot be newly configured.");

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

        foreach (var binding in triggerKinds.Bindings)
            await binding.SetEnabledAsync(id, enabled, ct);

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
        string Kind, string Label, string? Health = null, bool Failing = false);

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

    /// <summary>The whole platform's pipeline picture: every configuration and every chain between them.</summary>
    public sealed record GlobalFlow(
        IReadOnlyList<GlobalFlowNode> Nodes,
        IReadOnlyList<GlobalFlowEdge> Edges);

    public sealed record GlobalFlowNode(
        Guid Id,
        string DisplayName,
        string WorkflowType,
        bool Enabled,
        IReadOnlyList<TriggerSummary> Triggers,
        IReadOnlyList<string> Outputs);

    /// <summary>One chain: the producer's artifact of <see cref="ArtifactType"/> dispatches the consumer.</summary>
    public sealed record GlobalFlowEdge(Guid ProducerId, Guid ConsumerId, string ArtifactType);

    /// <summary>
    /// Assembles the global pipeline graph: every configuration as a node, and an edge for
    /// each artifact-chain trigger from every configuration whose workflow declares the
    /// consumed artifact type as an output.
    /// </summary>
    public async Task<GlobalFlow> GlobalFlowAsync(CancellationToken ct = default)
    {
        var overviews = await ListAsync(ct);

        var outputsByType = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var record in (await workflowSchemas.ReadAsync(ct)).ToList())
            outputsByType[record.WorkflowType] =
                (ParseSchema(record.SchemaJson)?.Outputs ?? []).Select(o => o.Name).ToList();

        var nodes = overviews
            .Select(o => new GlobalFlowNode(
                o.Id, o.DisplayName, o.WorkflowType, o.Enabled, o.TriggerSummaries,
                outputsByType.GetValueOrDefault(o.WorkflowType) ?? []))
            .ToList();

        var nodesById = nodes.ToDictionary(n => n.Id);
        var edges = new List<GlobalFlowEdge>();
        foreach (var chain in (await artifactTriggers.ReadAsync(ct)).ToList())
        {
            if (chain.WorkflowConfigurationId is not { } consumerId || !nodesById.ContainsKey(consumerId))
                continue;
            foreach (var producer in nodes.Where(n =>
                         n.Id != consumerId && n.Outputs.Contains(chain.ArtifactType, StringComparer.Ordinal)))
                edges.Add(new GlobalFlowEdge(producer.Id, consumerId, chain.ArtifactType));
        }

        return new GlobalFlow(nodes, edges.Distinct().ToList());
    }

    /// <summary>
    /// Dispatches one run of a configuration straight from the dashboard. The workflow's
    /// declared inputs decide what a run needs: required inputs must be provided, optional
    /// ones may stay empty, and a workflow declaring none runs without any input. Each
    /// provided input lands in the dispatch context under its declared name; one named
    /// "instruction" additionally lands as the mail-shaped Body. The command references the
    /// configuration; the Steering Instance resolves workflow, package, and slots exactly
    /// like a trigger dispatch. Policy-checked and audited.
    /// </summary>
    public async Task<Guid> RunNowAsync(
        string actor, Guid? actorPrincipalId, Guid configurationId,
        IReadOnlyDictionary<string, string>? inputs = null, CancellationToken ct = default)
    {
        var record = await configurations.ReadAsync(configurationId, ct)
                     ?? throw new InvalidOperationException("Unknown workflow configuration.");
        if (!record.Enabled)
            throw new InvalidOperationException("This configuration is disabled — enable it first.");
        if (!await WorkflowTypeEnabledAsync(record.WorkflowType, ct))
            throw new InvalidOperationException(
                $"An administrator disabled the workflow '{record.WorkflowType}' platform-wide.");

        var schema = ParseSchema((await workflowSchemas.ReadAsync(
            WorkflowSchemaRecord.IdFor(record.WorkflowType), ct))?.SchemaJson);

        // A workflow can only be triggered in ways it declares; declaring nothing keeps
        // schema-less and pre-declaration workflows manually startable.
        if (schema?.Triggers is { Count: > 0 } declaredTriggers
            && declaredTriggers.All(t => t.Kind != TriggerDeclaration.Manual))
            throw new InvalidOperationException(
                "This workflow does not support manual runs — it declares no 'manual' trigger.");

        var missing = DeclaredInputsOf(schema)
            .Where(input => input.Required
                            && string.IsNullOrWhiteSpace(inputs?.GetValueOrDefault(input.Name)))
            .Select(input => input.Label)
            .ToList();
        if (missing.Count > 0)
            throw new ArgumentException(
                $"This workflow requires input: {string.Join(", ", missing)}.", nameof(inputs));

        if (actorPrincipalId is { } principalId)
        {
            var decision = await policyEngine.EvaluateAsync(
                new PolicyContext(principalId, PermissionActions.WorkflowTrigger, record.Name)
                    { WorkflowType = record.WorkflowType }, ct);
            if (!decision.Allowed)
                throw new InvalidOperationException($"workflow.trigger denied: {decision.Reason}");
        }

        var context = new Dictionary<string, string>
        {
            ["Title"] = $"Run of '{record.DisplayName}'"
        };
        foreach (var (name, value) in inputs ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            context[name] = value.Trim();
            if (name == "instruction")
                context["Body"] = value.Trim(); // the mail-shaped alias existing workflows read
        }

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), null, null, context, actorPrincipalId, configurationId);
        await messageBus.PublishAsync(platformHostSettings.Value.CommandQueueName, command, ct);

        // Inputs are user input, not secrets — but keep the audit to the reference.
        await auditLog.AppendAsync(actor, "workflow-configuration.run-now",
            record.Name, command.CommandId.ToString(), ct: ct);
        return command.CommandId;
    }

    /// <summary>
    /// The inputs a workflow's schema declares; an unknown schema falls back to one optional
    /// free-text instruction so legacy workflows stay manually startable with context.
    /// </summary>
    internal static IReadOnlyList<WorkflowInputDescriptor> DeclaredInputsOf(WorkflowSchema? schema)
        => schema is null
            ? [new WorkflowInputDescriptor("instruction", "Instruction",
                Required: false, "Optional context for the run (this workflow declares no inputs).")]
            : schema.Inputs;

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

        var triggers = (await ConfiguredTriggersAsync(ct))
            .Where(t => t.ConfigurationId == id)
            .Select(t => new FlowTrigger(t.Kind, t.FlowLabel, t.Health, t.Failing))
            .ToList();
        var chains = (await artifactTriggers.ReadAsync(ct)).ToList();

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

    /// <summary>The workflow packages registered on this platform, joined with their stored schemas and curation.</summary>
    public async Task<IReadOnlyList<RegisteredWorkflow>> RegisteredWorkflowsAsync(CancellationToken ct = default)
    {
        var packageRecords = (await workflowPackages.ReadAsync(ct)).ToList();
        var schemasByType = (await workflowSchemas.ReadAsync(ct)).ToList()
            .ToDictionary(r => r.WorkflowType, r => r.SchemaJson, StringComparer.Ordinal);
        var disabledTypes = (await workflowCatalog.ReadAsync(ct)).ToList()
            .Where(c => !c.Enabled)
            .Select(c => c.WorkflowType)
            .ToHashSet(StringComparer.Ordinal);

        return packageRecords
            .Select(p => ToRegisteredWorkflow(p, schemasByType.GetValueOrDefault(p.WorkflowType))
                with { Enabled = !disabledTypes.Contains(p.WorkflowType) })
            .OrderBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Enables or disables a workflow type platform-wide: a disabled type is not offered for
    /// new configurations and manual runs of it are rejected. Audited.
    /// </summary>
    public async Task SetWorkflowEnabledAsync(
        string actor, string workflowType, bool enabled, CancellationToken ct = default)
    {
        await workflowCatalog.SaveAsync(new WorkflowCatalogRecord
        {
            Id = WorkflowCatalogRecord.IdFor(workflowType),
            WorkflowType = workflowType,
            Enabled = enabled
        }, ct);
        await auditLog.AppendAsync(actor, "workflow-catalog.enabled-changed",
            workflowType, enabled ? "enabled" : "disabled", ct: ct);
    }

    private async Task<bool> WorkflowTypeEnabledAsync(string workflowType, CancellationToken ct)
        => (await workflowCatalog.ReadAsync(WorkflowCatalogRecord.IdFor(workflowType), ct))?.Enabled ?? true;

    /// <summary>
    /// Which workflow slots each provider can back, derived from schema slot contracts —
    /// the slot-instance editors offer exactly this instead of a hand-picked "slot kind":
    /// a provider no enabled workflow can use is not configurable.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderSlotTarget>>> SlotTargetsByProviderAsync(
        CancellationToken ct = default)
    {
        var workflows = (await RegisteredWorkflowsAsync(ct))
            .Where(w => w.Enabled && w.SchemaKnown)
            .ToList();
        var entries = await catalog.ListAsync(ct);

        return entries.ToDictionary(
            entry => entry.ProviderType,
            entry => (IReadOnlyList<ProviderSlotTarget>)workflows
                .SelectMany(w => w.Slots
                    .Where(s => s.Contract is { Length: > 0 } contract && entry.Implements(contract))
                    .Select(s => new ProviderSlotTarget(s.SlotName, w.DisplayName)))
                .Distinct()
                .ToList(),
            StringComparer.Ordinal);
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
        IReadOnlyList<string>? declaredTriggerKinds,
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

        var validationContext = new TriggerValidationContext(instancesById, actorPrincipalId);
        foreach (var trigger in draft.Triggers)
        {
            // A workflow can only be triggered in ways its schema declares (declaring nothing
            // keeps configurations of schema-less or pre-declaration workflows valid).
            if (declaredTriggerKinds is { Count: > 0 }
                && !declaredTriggerKinds.Contains(trigger.Kind, StringComparer.Ordinal))
            {
                errors.Add($"This workflow does not support '{trigger.Kind}' triggers.");
                continue;
            }

            if (triggerKinds.BindingOf(trigger.Kind) is not { } binding)
            {
                errors.Add($"Unknown trigger kind '{trigger.Kind}'.");
                continue;
            }

            errors.AddRange(binding.Validate(trigger, validationContext));
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
        foreach (var binding in triggerKinds.Bindings)
            await binding.RemoveExceptAsync(actor, configurationId, keptIds, ct);

        foreach (var trigger in draft.Triggers)
        {
            if (triggerKinds.BindingOf(trigger.Kind) is { } binding)
                await binding.SaveAsync(actor, actorPrincipalId, draft, configurationId, trigger, ct);
        }
    }

    private async Task RemoveTriggerRecordsAsync(string actor, Guid configurationId, CancellationToken ct)
    {
        foreach (var binding in triggerKinds.Bindings)
            await binding.RemoveExceptAsync(actor, configurationId, new HashSet<Guid>(), ct);
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

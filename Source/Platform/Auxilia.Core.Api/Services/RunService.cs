using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Dispatches runs to the runner pool. Both the "on the fly" and "from a stored configuration"
/// paths resolve here into a self-contained <see cref="RunWorkflowCommand"/> the runner can
/// execute without reading any Core store. The command carries a run-scoped resolution token —
/// never secrets — that the runner presents to resolve credentialed slots just-in-time.
/// </summary>
public sealed class RunService(
    IMessageBusClient bus,
    RunConfigurationService configurations,
    WorkflowTypeRegistryService workflowTypes,
    WorkflowSchemaReadService schemaReader,
    ProviderCatalogService providerCatalog,
    SlotCredentialResolver credentialResolver,
    SlotBindingSecrets bindingSecrets,
    ConnectorAccessPolicy connectorAccess,
    ConnectorService connectors,
    WorkspaceResourceService workspaceResources,
    AccessGrantEvaluator grantEvaluator,
    Auxilia.Governance.PrincipalDirectory principalDirectory,
    Auxilia.Governance.Policy.IDefaultResourceAccessPolicy defaultAccess,
    RunnerLivenessTracker runnerLiveness,
    EnvironmentBaseService environmentBases,
    Auxilia.UniversalDataAccess.IDataAccess<Data.CoreRunRecord> runs,
    Auxilia.Workflows.Messaging.WorkflowStatusPublisher statusPublisher,
    TimeProvider clock,
    IOptions<CoreApiSettings> settings,
    ILogger<RunService> logger,
    RunQuotaService? quotas = null)
{
    /// <summary>Prefix of the synthetic slot a workspace mount's auth connector is stashed under.</summary>
    internal const string MountAuthSlotPrefix = "mount-auth:";

    public async Task<RunAccepted> RunInlineAsync(RunRequest request, Guid? triggeredBy, CancellationToken ct)
        => await DispatchAsync(
            request.WorkflowType,
            new Dictionary<string, string>(request.Context ?? new Dictionary<string, string>()),
            // Inline Secret-kind settings are protected the moment they enter the Core — the
            // stash never holds them in the clear; the resolver decrypts just-in-time.
            await bindingSecrets.ProtectAsync(request.SlotBindings ?? [], ct), triggeredBy, ct);

    public async Task<RunAccepted> RunConfigurationAsync(
        Guid configurationId, Guid? triggeredBy, IReadOnlyDictionary<string, string>? context, CancellationToken ct)
    {
        var config = await configurations.GetForDispatchAsync(configurationId, ct)
                     ?? throw new KeyNotFoundException($"configuration '{configurationId}' not found");
        if (!config.Enabled)
            throw new InvalidOperationException($"configuration '{config.Name}' is disabled");

        // The stored configuration's context is the base; a trigger's runtime context (artifact id,
        // work-item id, mail fields) overlays it — runtime values win on a key collision.
        var merged = new Dictionary<string, string>(config.Context);
        if (context is not null)
            foreach (var (key, value) in context)
                merged[key] = value;

        return await DispatchAsync(
            config.WorkflowType, merged, config.SlotBindings, triggeredBy, ct, configurationId);
    }

    /// <summary>
    /// Re-dispatches a past run from its stored dispatch command: a FRESH command id and
    /// resolution token, the original bindings re-stashed under them (so JIT credential
    /// resolution works — reusing the old token against a new command id could not resolve),
    /// and the package coordinate re-resolved from the registry. The caller's eligibility for
    /// every bound connector, the tool matching, and the catalog grant gate are all re-checked
    /// against the fresh schema — a rerun is a new run, not a replay of old trust.
    /// </summary>
    public async Task<RunAccepted> RerunAsync(
        Data.CoreRunRecord run, Guid? triggeredBy, CancellationToken ct,
        IReadOnlyDictionary<string, string>? contextOverlay = null,
        bool seesAllConfigurations = false)
    {
        if (quotas is not null)
            await quotas.EnsureCanDispatchAsync(triggeredBy, ct);
        if (run.DispatchCommandJson is not { Length: > 0 } commandJson
            || System.Text.Json.JsonSerializer.Deserialize<RunWorkflowCommand>(commandJson) is not { } original
            || run.CommandId is not { } originalCommandId)
            throw new InvalidOperationException("this run carries no stored dispatch command to rerun from");
        var stash = await credentialResolver.GetStashAsync(originalCommandId, ct)
                    ?? throw new InvalidOperationException("this run's resolution context is no longer stored");
        var workflowType = original.WorkflowType ?? run.WorkflowType;

        foreach (var connectorId in stash.Bindings
                     .Where(b => b.ConnectorId is not null)
                     .Select(b => b.ConnectorId!.Value)
                     .Distinct())
            if (!await connectorAccess.CanUseAsync(connectorId, triggeredBy, ct))
                throw new ConnectorAccessDeniedException(connectorId);

        // The same gates the original dispatch applied to what the bindings EXPANDED from: every
        // workspace the mounts came from, and — for a run of a personal configuration — the
        // configuration's visibility (its inline settings are the owner's, not the rerunner's).
        foreach (var workspaceId in stash.WorkspaceIds.Distinct())
            if (!await workspaceResources.CanUseAsync(workspaceId, triggeredBy, ct))
                throw new WorkspaceAccessDeniedException(
                    (await workspaceResources.GetAsync(workspaceId, ct))?.Name ?? workspaceId.ToString("D"));
        if (stash.ConfigurationId is { } configurationId && triggeredBy is not null
            && !await configurations.IsVisibleAsync(
                configurationId, new ConfigurationViewer(triggeredBy, seesAllConfigurations), ct))
            throw new RunAccessDeniedException(
                $"not permitted to rerun configuration '{configurationId:D}' — it is not visible to you or no longer exists");

        // The stashed bindings must ALSO still pass today's tool matching and catalog grant gate
        // against the freshly resolved schema — the same per-binding gate as a first dispatch.
        var providedTools = await ProvidedToolsAsync(workflowType, ct);
        foreach (var binding in stash.Bindings)
            // A synthetic mount-auth slot carries only the mount's credential connector; its
            // mount provider's entry was gated when the workspace binding expanded — the
            // connector eligibility check above is the gate that applies to it here.
            if (!binding.SlotName.StartsWith(MountAuthSlotPrefix, StringComparison.Ordinal))
                await ValidateBindingAsync(workflowType, binding, providedTools, triggeredBy, ct);

        var commandId = Guid.NewGuid();
        var resolutionToken = Guid.NewGuid().ToString("N");
        var packageUri = await workflowTypes.ResolvePackageUriForDispatchAsync(
            workflowType, commandId, resolutionToken, ct);
        var context = original.Context;
        if (contextOverlay is { Count: > 0 })
        {
            var merged = new Dictionary<string, string>(original.Context);
            foreach (var (key, value) in contextOverlay)
                merged[key] = value;
            context = merged;
        }
        var command = original with
        {
            CommandId = commandId,
            ResolutionToken = resolutionToken,
            WorkflowPackageUri = packageUri,
            Context = context,
            // Re-resolve the schema like the package coordinate — the registry may have a
            // fresher one than the original dispatch carried.
            SchemaJson = (await workflowTypes.GetRecordAsync(workflowType, ct))?.SchemaJson
                         ?? original.SchemaJson,
        };
        var rerunCommandJson = System.Text.Json.JsonSerializer.Serialize(ResolutionTokens.Redacted(command));
        await credentialResolver.StashAsync(
            commandId, resolutionToken, stash.Bindings, triggeredBy ?? stash.TriggeredBy,
            rerunCommandJson, stash.WorkspaceIds, stash.ConfigurationId, ct);
        await RecordDispatchedAsync(commandId, command.WorkflowType ?? run.WorkflowType, rerunCommandJson, ct);
        await bus.PublishAsync(settings.Value.RunCommandQueue, command, ct);
        logger.LogInformation(
            "Re-dispatched run. OriginalRunId={OriginalRunId} NewCommandId={CommandId} WorkflowType={WorkflowType}",
            run.Id, commandId, command.WorkflowType);
        return new RunAccepted(commandId, commandId);
    }

    /// <summary>
    /// Catalog-entry access gate: an entry with grants admits only the listed subjects; an entry
    /// WITHOUT grants follows the platform default (restricted = administrators only, open =
    /// everyone). Administrators always pass, and system dispatches without a principal (the
    /// approval pipeline) are exempt — the Core itself is the actor there; a failover re-dispatch
    /// acts as the original triggering principal and is gated like them.
    /// </summary>
    private async Task EnsureMayUseCatalogEntryAsync(
        ProviderCatalogEntry entry, Guid? triggeredBy, CancellationToken ct)
    {
        if (triggeredBy is not { } principal)
            return;
        if (await principalDirectory.IsAdministratorAsync(principal, ct))
            return;
        if (entry.Grants.Count == 0)
        {
            if (!await defaultAccess.IsRestrictedAsync(ct))
                return;
            throw new RunAccessDeniedException(
                $"provider '{entry.ProviderType}' carries no grants and the platform default "
                + "is restricted — only administrators may bind it until access is granted");
        }
        if (!await grantEvaluator.IsGrantedAsync(
                System.Text.Json.JsonSerializer.Serialize(entry.Grants), principal, ct))
            throw new RunAccessDeniedException(
                $"not permitted to use provider '{entry.ProviderType}'");
    }

    /// <summary>
    /// Requests cancellation of a run. A run no runner has claimed yet (still <c>Dispatched</c>)
    /// is cancelled by the Core itself — the terminal transition as a versioned compare-and-swap,
    /// the resolution stash deleted so a late launch cannot resolve credentials (the same
    /// anti-execution measure as the claim-timeout sweep), and a <c>Cancelled</c> status event;
    /// forwarding to the runner pool would only be dropped there. Any other run is forwarded
    /// over the cancel queue; the owning container consumes it and stops.
    /// </summary>
    public async Task CancelAsync(Guid runId, CancellationToken ct)
    {
        if (await runs.ReadAsync(runId, ct) is { State: RunStates.Dispatched } dispatched)
        {
            var won = await runs.TrySaveAsync(dispatched with
            {
                State = RunStates.Cancelled,
                ErrorMessage = "cancelled-before-claim",
                UpdatedUtc = clock.GetUtcNow(),
                CompletedUtc = clock.GetUtcNow()
            }, dispatched.Version, ct);
            if (won)
            {
                await statusPublisher.PublishAsync(
                    dispatched.Id, dispatched.WorkflowType, RunStates.Cancelled,
                    "cancelled-before-claim", commandId: dispatched.CommandId, ct: ct);
                // The stash stays: the resolver refuses every resolution for a terminal run, so
                // a late launch of this command cannot resolve credentials, while a rerun of
                // the cancelled run remains possible.
                logger.LogInformation("Cancelled unclaimed dispatch. RunId={RunId}", runId);
                return;
            }
            // Lost the swap: a claim (or another verdict) landed meanwhile — fall through and
            // forward, the per-instance cancel queue is now the right place.
        }
        await bus.PublishAsync(settings.Value.CancelCommandQueue, new CancelWorkflowCommand(runId), ct);
        logger.LogInformation("Requested cancel. RunId={RunId}", runId);
    }

    private async Task<RunAccepted> DispatchAsync(
        string workflowType, Dictionary<string, string> context,
        IReadOnlyList<SlotBinding> slotBindings, Guid? triggeredBy, CancellationToken ct,
        Guid? configurationId = null)
    {
        if (quotas is not null)
            await quotas.EnsureCanDispatchAsync(triggeredBy, ct);

        // Fail fast when nobody can execute the run: runners announce themselves over bus
        // heartbeats, so a dispatch with no live runner would queue silently and the caller would
        // watch a dead stream. Runners are deployment-owned — the Core never starts one itself.
        if (!settings.Value.AllowDispatchWithoutRunner
            && !runnerLiveness.AnyAliveSince(
                clock.GetUtcNow() - TimeSpan.FromSeconds(settings.Value.HeartbeatTimeoutSeconds)))
            throw new InvalidOperationException(
                "no live Core.Runner is connected — the run cannot execute. Start a runner "
                + "(or set CoreApi:AllowDispatchWithoutRunner to queue deliberately).");

        // Tool matching and the catalog grant gate run per binding INSIDE the loop below, after
        // workspace expansion — a workspace reference only acquires its provider type when the
        // stored resource expands, and both checks must cover those bindings too.
        var providedTools = await ProvidedToolsAsync(workflowType, ct);

        // Bindings of providers that mount into the workspace become generic workspace mounts:
        // the binding's settings are re-keyed by the provider's declared setting ROLES (a pure
        // data transform — only the execution plane interprets the role vocabulary). A mount's
        // credential connector is stashed under a synthetic slot the runner resolves JIT; the
        // mount settings themselves are non-secret and ride the command.
        var mounts = new List<WorkspaceMountDispatch>();
        var environmentCapabilities = new List<string>();
        var environmentBases =
            new Dictionary<string, IReadOnlyList<EnvironmentBaseRef>>(StringComparer.OrdinalIgnoreCase);
        var pluginBindings = new List<SlotBinding>();
        var stashedBindings = new List<SlotBinding>();
        var workspaceIds = new List<Guid>();
        foreach (var boundSlot in slotBindings)
        {
            var binding = boundSlot;
            // A workspace reference expands LIVE at dispatch: the stored resource supplies the
            // provider type, settings, and credential connector — so editing the workspace once
            // (branch, setup script, …) applies to every configuration referencing it. Binding-
            // level settings/connector override per use; access is gated like connectors.
            if (binding.WorkspaceId is { } workspaceId)
            {
                var workspace = await workspaceResources.GetAsync(workspaceId, ct)
                    ?? throw new KeyNotFoundException($"workspace '{workspaceId:D}' does not exist");
                if (!await workspaceResources.CanUseAsync(workspaceId, triggeredBy, ct))
                    throw new WorkspaceAccessDeniedException(workspace.Name);
                workspaceIds.Add(workspaceId);
                var mergedSettings = new Dictionary<string, string>(workspace.Settings);
                foreach (var (key, value) in binding.Settings ?? new Dictionary<string, string>())
                    mergedSettings[key] = value;
                binding = binding with
                {
                    ProviderType = workspace.ProviderType,
                    ConnectorId = binding.ConnectorId ?? workspace.ConnectorId,
                    Settings = mergedSettings,
                };
            }

            var entry = await ValidateBindingAsync(workflowType, binding, providedTools, triggeredBy, ct);
            // Environment-composing bindings are pure selections: the provider type IS the
            // capability id — no plugin, no credential, interpreted only by the runner.
            if (entry is { ComposesEnvironment: true })
            {
                if (!environmentCapabilities.Contains(entry.ProviderType))
                    environmentCapabilities.Add(entry.ProviderType);
                if (entry.EnvironmentBases is { Count: > 0 } envBases)
                    environmentBases[entry.ProviderType] = envBases;
                continue;
            }
            if (entry is not { MountsIntoWorkspace: true })
            {
                pluginBindings.Add(binding);
                stashedBindings.Add(binding);
                continue;
            }

            var mountId = UniqueMountId(mounts, binding.SlotName);
            var settingsByRole = entry.Descriptors
                .Where(d => d.Role is { Length: > 0 }
                            && binding.Settings?.TryGetValue(d.Key, out _) == true)
                .ToDictionary(d => d.Role!, d => binding.Settings![d.Key]);
            string? authSlot = null;
            if (binding.ConnectorId is not null)
            {
                authSlot = MountAuthSlotPrefix + mountId;
                stashedBindings.Add(new SlotBinding(authSlot, ConnectorId: binding.ConnectorId));
            }
            mounts.Add(new WorkspaceMountDispatch(mountId, entry.ProviderType, settingsByRole, authSlot));
        }

        // One run composes ONE container image on ONE base — but a layer may carry a variant per
        // base. The selection is viable only if some base is supported by EVERY base-declaring
        // layer and, on that base, every version pin agrees. Layers without declared bases
        // (runner-static overrides) cannot be checked here and are left to the runner's composition.
        if (environmentBases.Count > 0)
        {
            var candidateBases = environmentBases.Values
                .Select(refs => refs.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase))
                .Aggregate((intersection, next) =>
                {
                    intersection.IntersectWith(next);
                    return intersection;
                });
            bool VersionsAgree(string baseName) => environmentBases.Values
                .Select(refs => refs.First(r =>
                    string.Equals(r.Name, baseName, StringComparison.OrdinalIgnoreCase)).Version)
                .Where(v => v is { Length: > 0 })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() <= 1;
            if (!candidateBases.Any(VersionsAgree))
                throw new InvalidOperationException(
                    "environment capabilities have no common base — "
                    + string.Join(", ", environmentBases.Select(b =>
                        $"'{b.Key}' ({string.Join("|", b.Value.Select(r => r.Version is { Length: > 0 } ? $"{r.Name}/{r.Version}" : r.Name))})"))
                    + ". One run composes one image on one base; every selected layer must share "
                    + "a base whose version pins agree.");
        }

        // Gate identity-linked connectors — including each repo's auth connector: the triggering
        // principal must be allowed to use every connector this run binds. Company connectors pass
        // freely; a personal connector admits only its owner and granted subjects.
        foreach (var connectorId in stashedBindings
                     .Where(b => b.ConnectorId is not null)
                     .Select(b => b.ConnectorId!.Value)
                     .Distinct())
            if (!await connectorAccess.CanUseAsync(connectorId, triggeredBy, ct))
                throw new ConnectorAccessDeniedException(connectorId);

        // The Core is the single authorization authority: the caller was already authorized here,
        // so the command is dispatched WITHOUT a RequestedBy principal. The runner trusts
        // Core-dispatched commands and cannot resolve a Core-database principal against its own.
        var commandId = Guid.NewGuid();

        // Build the run's slot→connector references under a run-scoped resolution token; the runner
        // presents the token to resolve credentialed slots (and repo auth) JIT. No secrets travel in
        // the command — only the provider types, so the runner can load the matching slot plugins.
        var resolutionToken = Guid.NewGuid().ToString("N");

        // The registry — not the caller — owns the package coordinate: only a registered, Active
        // (signature-trusted) type dispatches. A Core-stored package resolves to a token-authorized
        // download URL for this run.
        var packageUri = await workflowTypes.ResolvePackageUriForDispatchAsync(
            workflowType, commandId, resolutionToken, ct);

        // The runner ships each provider's plugin into the container at LAUNCH, so it must know
        // every provider type up front — including those hidden behind connector references (the
        // connector's provider type is not a secret; its settings stay Core-side until JIT).
        // Workspace-mount bindings are consumed by the runner itself and ship no plugin.
        var connectorProviderTypes = new List<string>();
        foreach (var connectorId in pluginBindings
                     .Where(b => string.IsNullOrEmpty(b.ProviderType) && b.ConnectorId is not null)
                     .Select(b => b.ConnectorId!.Value)
                     .Distinct())
            if ((await connectors.GetAsync(connectorId, ct))?.ProviderType is { Length: > 0 } providerType)
                connectorProviderTypes.Add(providerType);
        var providerTypes = pluginBindings
            .Where(b => !string.IsNullOrEmpty(b.ProviderType))
            .Select(b => b.ProviderType!)
            .Concat(connectorProviderTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var command = new RunWorkflowCommand(
            commandId, workflowType, packageUri, context,
            RequestedBy: null, WorkflowConfigurationId: null, ResolutionToken: resolutionToken,
            SlotProviderTypes: providerTypes,
            WorkspaceMounts: mounts.Count > 0 ? mounts : null,
            EnvironmentCapabilities: environmentCapabilities.Count > 0 ? environmentCapabilities : null,
            // The registry's inspected schema rides along so a FRESH runner decides terminal/
            // network/repository questions from it on the type's very first dispatch — the
            // runner-side store only fills on a run's own registration, which is too late.
            SchemaJson: (await workflowTypes.GetRecordAsync(workflowType, ct))?.SchemaJson,
            // The runtime-spawnable base snapshot, PINNED BY CONFIGURATION: only the base refs
            // the run's context selects (context key "pod-bases", fixable in the stored
            // configuration like any input) resolve into the map — default-deny when absent —
            // and a catalog edit never changes what an in-flight run may start.
            PodBaseImagesJson: await SerializeSpawnableBasesAsync(context, ct));

        // Stash the resolution context AND the dispatch command itself (keyed by CommandId), so an
        // orphaned run can be re-dispatched once on failover without the Core reading the runner's
        // DB. The stored copy is redacted: a re-dispatch mints a fresh token and package URL, so
        // only the bus copy the runner receives carries the plaintext capability.
        var storedCommandJson = System.Text.Json.JsonSerializer.Serialize(ResolutionTokens.Redacted(command));
        await credentialResolver.StashAsync(
            commandId, resolutionToken, stashedBindings, triggeredBy, storedCommandJson,
            workspaceIds, configurationId, ct);
        await RecordDispatchedAsync(commandId, workflowType, storedCommandJson, ct);

        await bus.PublishAsync(settings.Value.RunCommandQueue, command, ct);
        logger.LogInformation(
            "Dispatched run. CommandId={CommandId} WorkflowType={WorkflowType} WorkspaceMounts={MountCount}",
            commandId, workflowType, mounts.Count);
        return new RunAccepted(commandId, commandId);
    }

    /// <summary>Context key selecting the run's spawnable bases (comma-separated base refs).</summary>
    internal const string PodBasesContextKey = "pod-bases";

    private async Task<string?> SerializeSpawnableBasesAsync(
        IReadOnlyDictionary<string, string> context, CancellationToken ct)
    {
        if (!context.TryGetValue(PodBasesContextKey, out var selection)
            || string.IsNullOrWhiteSpace(selection))
            return null;
        var requested = selection
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var images = (await environmentBases.SpawnableImagesAsync(ct))
            .Where(kv => requested.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return images.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(images);
    }

    /// <summary>
    /// The run exists from the moment it is accepted: a <see cref="RunStates.Dispatched"/> record
    /// keyed by the command id (the only id that exists before a runner claims) makes a
    /// never-claimed dispatch visible to every read surface and to the failover monitor's
    /// claim-timeout sweep, instead of leaving no trace. The runner's claim event rekeys it to the
    /// instance id. Record before bus publish: a publish that never happens still leaves a
    /// sweepable Dispatched row, never a silent loss.
    /// </summary>
    private async Task RecordDispatchedAsync(
        Guid commandId, string workflowType, string commandJson, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await runs.SaveAsync(new Data.CoreRunRecord
        {
            Id = commandId,
            WorkflowType = workflowType,
            State = RunStates.Dispatched,
            CreatedUtc = now,
            UpdatedUtc = now,
            CommandId = commandId,
            DispatchCommandJson = commandJson
        }, ct);
        await statusPublisher.PublishAsync(
            commandId, workflowType, RunStates.Dispatched, commandId: commandId, ct: ct);
    }

    /// <summary>
    /// The tool set the workflow's image provides per its registered schema; null when no schema
    /// is registered yet = nothing to check against (the same blind-trust window the registry
    /// approval surfaces).
    /// </summary>
    private async Task<HashSet<string>?> ProvidedToolsAsync(string workflowType, CancellationToken ct)
        => (await schemaReader.GetSchemaAsync(workflowType, ct))?
            .ProvidedTools.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The per-binding dispatch gate, shared by first dispatch and rerun. Rejects a binding whose
    /// provider requires tools the workflow's image does not provide — the data-driven successor
    /// of the retired per-slot provider-type whitelist: the provider's manifest declares what it
    /// needs in the container, the schema declares what the image bundles, and the Core only
    /// intersects the two. Then applies the ONE grant gate for every catalog-curated resource a
    /// run binds — slot providers, environment layers, and workspace-mounting providers alike.
    /// </summary>
    private async Task<ProviderCatalogEntry?> ValidateBindingAsync(
        string workflowType, SlotBinding binding, IReadOnlySet<string>? providedTools,
        Guid? triggeredBy, CancellationToken ct)
    {
        var entry = await ResolveCatalogEntryAsync(binding, ct);
        if (entry is null)
            return null;
        if (providedTools is not null && entry.RequiredTools.Count > 0)
        {
            var missing = entry.RequiredTools.Where(t => !providedTools.Contains(t)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"slot '{binding.SlotName}' of '{workflowType}' cannot bind provider "
                    + $"'{entry.ProviderType}' — it requires tool(s) the workflow's image does "
                    + $"not provide: {string.Join(", ", missing)}");
        }
        await EnsureMayUseCatalogEntryAsync(entry, triggeredBy, ct);
        return entry;
    }

    /// <summary>The catalog entry behind a binding — inline provider type or the connector's.</summary>
    private async Task<ProviderCatalogEntry?> ResolveCatalogEntryAsync(SlotBinding binding, CancellationToken ct)
    {
        var providerType = binding.ProviderType;
        if (string.IsNullOrEmpty(providerType) && binding.ConnectorId is { } connectorId)
            providerType = (await connectors.GetAsync(connectorId, ct))?.ProviderType;
        if (string.IsNullOrEmpty(providerType))
            return null;
        return await providerCatalog.FindAsync(providerType, ct);
    }

    /// <summary>A slot bound several times (AllowMultiple) gets suffixed mount ids: slot, slot-2, …</summary>
    private static string UniqueMountId(IReadOnlyList<WorkspaceMountDispatch> mounts, string slotName)
    {
        var count = mounts.Count(m =>
            m.MountId == slotName
            || m.MountId.StartsWith(slotName + "-", StringComparison.Ordinal));
        return count == 0 ? slotName : $"{slotName}-{count + 1}";
    }
}

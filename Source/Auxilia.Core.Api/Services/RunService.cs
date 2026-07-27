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
    ConnectorAccessPolicy connectorAccess,
    ConnectorService connectors,
    RunnerLivenessTracker runnerLiveness,
    TimeProvider clock,
    IOptions<CoreApiSettings> settings,
    ILogger<RunService> logger)
{
    /// <summary>Prefix of the synthetic slot a workspace mount's auth connector is stashed under.</summary>
    internal const string MountAuthSlotPrefix = "mount-auth:";

    public Task<RunAccepted> RunInlineAsync(RunRequest request, Guid? triggeredBy, CancellationToken ct)
        => DispatchAsync(
            request.WorkflowType,
            new Dictionary<string, string>(request.Context ?? new Dictionary<string, string>()),
            request.SlotBindings ?? [], triggeredBy, ct);

    public async Task<RunAccepted> RunConfigurationAsync(
        Guid configurationId, Guid? triggeredBy, IReadOnlyDictionary<string, string>? context, CancellationToken ct)
    {
        var config = await configurations.GetAsync(configurationId, ct)
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
            config.WorkflowType, merged, config.SlotBindings, triggeredBy, ct);
    }

    /// <summary>Requests cancellation of a run; the runner consumes the command and stops the container.</summary>
    public async Task CancelAsync(Guid runId, CancellationToken ct)
    {
        await bus.PublishAsync(settings.Value.CancelCommandQueue, new CancelWorkflowCommand(runId), ct);
        logger.LogInformation("Requested cancel. RunId={RunId}", runId);
    }

    private async Task<RunAccepted> DispatchAsync(
        string workflowType, Dictionary<string, string> context,
        IReadOnlyList<SlotBinding> slotBindings, Guid? triggeredBy, CancellationToken ct)
    {
        // Fail fast when nobody can execute the run: runners announce themselves over bus
        // heartbeats, so a dispatch with no live runner would queue silently and the caller would
        // watch a dead stream. Runners are deployment-owned — the Core never starts one itself.
        if (!settings.Value.AllowDispatchWithoutRunner
            && !runnerLiveness.AnyAliveSince(
                clock.GetUtcNow() - TimeSpan.FromSeconds(settings.Value.HeartbeatTimeoutSeconds)))
            throw new InvalidOperationException(
                "no live Core.Runner is connected — the run cannot execute. Start a runner "
                + "(or set CoreApi:AllowDispatchWithoutRunner to queue deliberately).");

        // A slot that narrows its admissible provider types is enforced here: the narrowing is the
        // workflow's own schema declaration (e.g. its image bundles exactly one agent CLI), so an
        // out-of-set binding could never execute and must fail the dispatch, not the run.
        await ValidateSlotProviderTypesAsync(workflowType, slotBindings, ct);

        // Bindings of providers that mount into the workspace become generic workspace mounts:
        // the binding's settings are re-keyed by the provider's declared setting ROLES (a pure
        // data transform — only the execution plane interprets the role vocabulary). A mount's
        // credential connector is stashed under a synthetic slot the runner resolves JIT; the
        // mount settings themselves are non-secret and ride the command.
        var mounts = new List<WorkspaceMountDispatch>();
        var pluginBindings = new List<SlotBinding>();
        var stashedBindings = new List<SlotBinding>();
        foreach (var binding in slotBindings)
        {
            var entry = await ResolveCatalogEntryAsync(binding, ct);
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
            .Distinct()
            .ToList();

        var command = new RunWorkflowCommand(
            commandId, workflowType, packageUri, context,
            RequestedBy: null, WorkflowConfigurationId: null, ResolutionToken: resolutionToken,
            SlotProviderTypes: providerTypes,
            WorkspaceMounts: mounts.Count > 0 ? mounts : null);

        // Stash the resolution context AND the dispatch command itself (keyed by CommandId), so an
        // orphaned run can be re-dispatched once on failover without the Core reading the runner's DB.
        await credentialResolver.StashAsync(
            commandId, resolutionToken, stashedBindings, triggeredBy,
            System.Text.Json.JsonSerializer.Serialize(command), ct);

        await bus.PublishAsync(settings.Value.RunCommandQueue, command, ct);
        logger.LogInformation(
            "Dispatched run. CommandId={CommandId} WorkflowType={WorkflowType} WorkspaceMounts={MountCount}",
            commandId, workflowType, mounts.Count);
        return new RunAccepted(commandId, commandId);
    }

    /// <summary>Rejects bindings whose provider type falls outside the slot's declared narrowing.</summary>
    private async Task ValidateSlotProviderTypesAsync(
        string workflowType, IReadOnlyList<SlotBinding> slotBindings, CancellationToken ct)
    {
        var schema = await schemaReader.GetSchemaAsync(workflowType, ct);
        var narrowedSlots = schema?.Slots
            .Where(s => s.ProviderTypes is { Count: > 0 })
            .ToDictionary(s => s.SlotName, s => s.ProviderTypes!, StringComparer.Ordinal);
        if (narrowedSlots is not { Count: > 0 })
            return;
        foreach (var binding in slotBindings)
        {
            if (!narrowedSlots.TryGetValue(binding.SlotName, out var admitted))
                continue;
            var providerType = binding.ProviderType;
            if (string.IsNullOrEmpty(providerType) && binding.ConnectorId is not null)
                providerType = (await connectors.GetAsync(binding.ConnectorId.Value, ct))?.ProviderType;
            if (providerType is { Length: > 0 }
                && !admitted.Contains(providerType, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"slot '{binding.SlotName}' of '{workflowType}' does not admit provider "
                    + $"'{providerType}' — the workflow declares: {string.Join(", ", admitted)}");
        }
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

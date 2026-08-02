using Auxilia.Core.Contracts;
using System.IO.Compression;
using System.Text.Json;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Subscribes to the <c>workflow.run-commands</c> queue. For each
/// <see cref="RunWorkflowCommand"/> it:
/// <list type="number">
/// <item>Downloads the workflow ZIP from <see cref="RunWorkflowCommand.WorkflowPackageUri"/>.</item>
/// <item>Verifies the package signature via <see cref="IWorkflowPackageVerifier"/>.</item>
/// <item>Extracts the ZIP to a temporary directory.</item>
/// <item>Registers the extracted path in <see cref="PendingWorkflowPackageStore"/> for the
///     announcement handler to consume.</item>
/// <item>Launches the workflow container via <see cref="IWorkflowLauncher"/>.</item>
/// </list>
/// </summary>
public sealed class WorkflowDispatcher(
    IMessageBusClient messageBus,
    IWorkflowLauncher launcher,
    IOptions<DockerWorkflowLauncherSettings> launcherSettings,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
    IHttpClientFactory httpClientFactory,
    IWorkflowPackageVerifier packageVerifier,
    PendingWorkflowPackageStore pendingPackages,
    SlotProviderRegistry providerRegistry,
    WorkflowInstanceTokenRegistry tokenRegistry,
    IPolicyEngine policyEngine,
    WorkflowInstanceRegistry instanceRegistry,
    WorkflowStatusPublisher statusPublisher,
    WorkflowSchemaStore schemaStore,
    WorkflowPackageStore packageStore,
    Auxilia.PlatformData.Artifacts.IArtifactStore artifactStore,
    NetworkPolicyResolver networkPolicyResolver,
    WorkspaceManager workspaceManager,
    IRepositoryAuthResolver repositoryAuthResolver,
    AuditLog auditLog,
    CoreRunnerInfo instanceInfo,
    ILogger<WorkflowDispatcher> logger)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var queueName = dispatcherSettings.Value.CommandQueueName;
        await messageBus.DeclareQueueAsync(queueName, ct);
        _subscription = await messageBus.SubscribeAsync<RunWorkflowCommand>(
            queueName, HandleAsync, ct);

        logger.LogInformation("WorkflowDispatcher started — listening on {QueueName}.", queueName);
    }

    private async Task HandleAsync(RunWorkflowCommand command, CancellationToken ct)
    {
        // The Core resolves configurations and hands us a self-contained run spec: workflow type,
        // package URI, context, the slot provider types to load, and a resolution token.
        var workflowType = command.WorkflowType ?? string.Empty;
        var packageUri = command.WorkflowPackageUri ?? string.Empty;

        logger.LogInformation(
            "Received RunWorkflowCommand. CommandId={CommandId} WorkflowType={WorkflowType} PackageUri={PackageUri}",
            command.CommandId, workflowType, packageUri);

        // The instance identity exists for the whole lifecycle — including pre-flight
        // rejections — so every outcome is visible in the dashboard.
        var issued = tokenRegistry.Issue(workflowType);
        var instanceId = issued.WorkflowInstanceId;
        await instanceRegistry.CreateAsync(
            instanceId, workflowType, "Received",
            instanceInfo.ServiceId, JsonSerializer.Serialize(command), ct: ct);
        // Claim transition: stamp the owning runner + originating command so a bus consumer can
        // attribute this run to us (and recover our stored dispatch command) without reading our DB.
        await statusPublisher.PublishAsync(instanceId, workflowType, "Received",
            ownerServiceId: instanceInfo.ServiceId, commandId: command.CommandId, ct: ct);

        // Pre-flight authorization: the trigger permission of the requesting principal.
        if (command.RequestedBy is { } principalId)
        {
            var decision = await policyEngine.EvaluateAsync(
                new PolicyContext(principalId, PermissionActions.WorkflowTrigger, command.CommandId.ToString())
                {
                    WorkflowType = workflowType
                }, ct);
            if (!decision.Allowed)
            {
                logger.LogWarning(
                    "Dispatch denied by policy. CommandId={CommandId} WorkflowType={WorkflowType} Principal={Principal} Reason={Reason}",
                    command.CommandId, workflowType, principalId, decision.Reason);
                await FailPreFlightAsync(instanceId, workflowType,
                    $"dispatch denied by policy: {decision.Reason}", ct);
                return;
            }
        }
        else if (dispatcherSettings.Value.RequirePrincipal)
        {
            logger.LogWarning(
                "Dispatch rejected: RunWorkflowCommand without RequestedBy principal while RequirePrincipal is enabled. CommandId={CommandId}",
                command.CommandId);
            await FailPreFlightAsync(instanceId, workflowType,
                "dispatch rejected: no requesting principal", ct);
            return;
        }

        var settings = launcherSettings.Value;

        // Pre-create the instance's exclusive response queue so configuration is never
        // delivered to a self-declared topic.
        await messageBus.DeclareQueueAsync(WorkflowQueues.ResponseQueueFor(instanceId), ct);

        // 5. Build env vars
        var env = new Dictionary<string, string>
        {
            ["RabbitMq__Host"]               = settings.RabbitMqHost,
            ["RabbitMq__Port"]               = settings.RabbitMqPort.ToString(),
            ["RabbitMq__UserName"]           = settings.RabbitMqUserName,
            ["RabbitMq__Password"]           = settings.RabbitMqPassword,
            [WorkflowEnvironmentVariables.RegistrationQueue] = dispatcherSettings.Value.RegistrationQueueName,
            [WorkflowEnvironmentVariables.AnnouncementQueue] = dispatcherSettings.Value.AnnouncementQueueName,
            [WorkflowEnvironmentVariables.SlotActivationQueue] = dispatcherSettings.Value.SlotActivationQueueName,
            [WorkflowEnvironmentVariables.ResourceProxyQueue] = dispatcherSettings.Value.ResourceProxyQueueName,
            [WorkflowEnvironmentVariables.InstanceId]        = instanceId.ToString("D"),
            [WorkflowEnvironmentVariables.InstanceToken]     = issued.Token,
            [WorkflowEnvironmentVariables.OutputDirectory]   = "/workflow-output",
        };

        // Per-run output directory: mounted into the container; declared outputs found
        // there are persisted to the artifact store when the run succeeds. The dispatcher
        // (and ArtifactPersister) always use the local view; the launcher gets the bind
        // source the Docker daemon can resolve (see ResolveOutputDirectoryBind).
        var outputDirectory = Path.Combine(
            dispatcherSettings.Value.RunOutputDirectory, instanceId.ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        var outputDirectoryBind = ResolveOutputDirectoryBind(dispatcherSettings.Value, instanceId);

        foreach (var (key, value) in command.Context)
            env[$"WORKFLOW_CONTEXT__{key.ToUpperInvariant()}"] = value;

        // Chained runs consume their predecessor's artifact from the run directory: the bus
        // carries only the reference (ArtifactId); the payload rides the output bind. The
        // 'consumed' subdirectory is never a declared output, so it is never re-persisted.
        if (command.Context.TryGetValue("ArtifactId", out var artifactIdRaw)
            && Guid.TryParse(artifactIdRaw, out var consumedArtifactId)
            && await artifactStore.OpenReadAsync(consumedArtifactId, ct) is { } artifactPayload)
        {
            await using (artifactPayload)
            {
                var consumedDir = Path.Combine(outputDirectory, "consumed");
                Directory.CreateDirectory(consumedDir);
                var fileName = (command.Context.GetValueOrDefault("ArtifactType") ?? "artifact") + ".json";
                await using var file = File.Create(Path.Combine(consumedDir, fileName));
                await artifactPayload.CopyToAsync(file, ct);
                env["WORKFLOW_CONSUMED_ARTIFACT"] = $"/workflow-output/consumed/{fileName}";
            }
        }

        // Merge extra environment variables (e.g. AUXILIA_DEVELOPER_MODE)
        if (settings.ExtraEnvironmentVariables is not null)
            foreach (var (key, value) in settings.ExtraEnvironmentVariables)
                env[key] = value;

        // Resolve slot-handler plugins for the run's provider types (sent by the Core). An
        // unregistered provider type fails pre-flight.
        var pluginFiles = new List<SlotPluginFile>();
        foreach (var providerType in (command.SlotProviderTypes ?? []).Distinct())
        {
            var dllPath = await providerRegistry.GetDllPathAsync(providerType, ct);
            if (dllPath is null)
            {
                var reason = $"run references unregistered slot provider '{providerType}'";
                logger.LogWarning(
                    "Dispatch rejected: {Reason}. CommandId={CommandId}", reason, command.CommandId);
                await auditLog.AppendAsync(
                    "steering-instance", "workflow.dispatch.rejected",
                    instanceId.ToString(), reason, ct: ct);
                await FailPreFlightAsync(instanceId, workflowType, reason, ct);
                return;
            }

            var manifestPath = Path.ChangeExtension(dllPath, null) + ".manifest.json";
            // A manifest opting into BundleDependencies ships its whole NuGet closure (every
            // sibling non-Auxilia DLL) — shared Auxilia contracts always come from the image.
            IReadOnlyList<string>? dependencies = null;
            try
            {
                var manifest = File.Exists(manifestPath)
                    ? JsonSerializer.Deserialize<Auxilia.Workflows.PluginManifest>(
                        await File.ReadAllTextAsync(manifestPath, ct))
                    : null;
                if (manifest is { BundleDependencies: true })
                    dependencies = Directory.GetFiles(Path.GetDirectoryName(dllPath)!, "*.dll")
                        .Where(f => !string.Equals(f, dllPath, StringComparison.OrdinalIgnoreCase)
                                    && (!Path.GetFileName(f).StartsWith("Auxilia", StringComparison.OrdinalIgnoreCase)
                                        // A plugin's OWN Auxilia assemblies (its adapter) are not
                                        // in any image — the manifest names them explicitly.
                                        || manifest.BundledAuxiliaAssemblies?.Contains(
                                            Path.GetFileName(f), StringComparer.OrdinalIgnoreCase) == true))
                        .ToList();
            }
            catch (JsonException)
            {
                // Malformed sidecar — ship the plugin alone; loading will surface the real error.
            }
            pluginFiles.Add(new SlotPluginFile(dllPath, manifestPath, dependencies));
        }

        if (pluginFiles.Count > 0)
            logger.LogInformation(
                "Resolved {Count} slot plugin file(s) for {WorkflowType}: {ProviderTypes}",
                pluginFiles.Count, workflowType,
                string.Join(", ", pluginFiles.Select(f => f.DllPath)));

        // Resolve environment-capability layers (capability id → Dockerfile fragment). A locally
        // configured layer is the host's override; anything else is fetched from the Core's
        // admin-managed layer store, authorized by the run's resolution token. A capability with
        // neither fails pre-flight — never launch with a silently wrong environment.
        var environmentLayers = new List<string>();
        foreach (var capability in (command.EnvironmentCapabilities ?? []).Distinct())
        {
            string? fragment = null;
            if (launcherSettings.Value.EnvironmentLayers.TryGetValue(capability, out var fragmentPath)
                && File.Exists(fragmentPath))
                fragment = await File.ReadAllTextAsync(fragmentPath, ct);
            else
                fragment = await FetchEnvironmentLayerAsync(capability, command, ct);

            if (fragment is null)
            {
                var reason = $"run selects environment capability '{capability}' with no layer — "
                             + "neither configured on this runner nor managed in the Core";
                logger.LogWarning(
                    "Dispatch rejected: {Reason}. CommandId={CommandId}", reason, command.CommandId);
                await auditLog.AppendAsync(
                    "steering-instance", "workflow.dispatch.rejected",
                    instanceId.ToString(), reason, ct: ct);
                await FailPreFlightAsync(instanceId, workflowType, reason, ct);
                return;
            }
            environmentLayers.Add(fragment);
        }
        if (environmentLayers.Count > 0)
            logger.LogInformation(
                "Resolved {Count} environment layer(s) for {WorkflowType}: {Capabilities}",
                environmentLayers.Count, workflowType,
                string.Join(", ", command.EnvironmentCapabilities!.Distinct()));

        async Task<string?> FetchEnvironmentLayerAsync(
            string capability, RunWorkflowCommand cmd, CancellationToken token)
        {
            if (dispatcherSettings.Value.CoreApiBaseAddress is not { Length: > 0 } baseAddress)
                return null;
            try
            {
                var http = httpClientFactory.CreateClient("workflow-packages");
                var url = $"{baseAddress.TrimEnd('/')}/api/environment-layers/"
                          + $"{Uri.EscapeDataString(capability)}/content"
                          + $"?runId={cmd.CommandId}&token={Uri.EscapeDataString(cmd.ResolutionToken ?? "")}";
                using var response = await http.GetAsync(url, token);
                if (!response.IsSuccessStatusCode)
                    return null;
                var fragment = await response.Content.ReadAsStringAsync(token);

                // Environments are build-time code: verify the Core's signature against the
                // runner's trusted keys (permissive only when no trust keys are configured).
                var signature = response.Headers.TryGetValues("X-Auxilia-Signature", out var sigValues)
                    ? sigValues.FirstOrDefault() : null;
                var publisherKey = response.Headers.TryGetValues("X-Auxilia-Publisher-Key", out var keyValues)
                    ? keyValues.FirstOrDefault() : null;
                if (!EnvironmentFragmentVerifier.Verify(
                        fragment, signature, publisherKey,
                        dispatcherSettings.Value.TrustedEnvironmentSigningKeys))
                {
                    logger.LogWarning(
                        "Rejected environment layer '{Capability}': signature missing, untrusted, or invalid.",
                        capability);
                    return null;
                }
                return fragment;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex,
                    "Could not fetch environment layer '{Capability}' from the Core.", capability);
                return null;
            }
        }

        // Effective network policy (ARCHITECTURE §10): manifest baseline (last stored schema)
        // merged with run-configuration extras, clamped by platform policy, audited per run.
        var schema = await schemaStore.GetSchemaAsync(workflowType, ct);
        var networkPolicy = networkPolicyResolver.Resolve(
            schema?.NetworkEndpoints ?? [], command.Context, dispatcherSettings.Value);
        await auditLog.AppendAsync(
            "steering-instance", "workflow.network-policy",
            instanceId.ToString(), networkPolicy.Mode.ToString(),
            JsonSerializer.Serialize(new
            {
                endpoints = networkPolicy.AllowedEndpoints,
                note = networkPolicy.Note
            }), ct);

        // Workspace mounts (ARCHITECTURE §9): repos declared in the stored schema plus the run's
        // generic workspace-mount bindings are prepared by the Workspace Manager and bind-mounted
        // at /workspace. Mount settings arrive keyed by the provider's declared roles — THIS is the
        // plane that interprets them (WorkspaceMountRoles). A mount's auth (when present) is
        // resolved from the Core just-in-time here — the credential is injected into the clone URL,
        // used to clone, and never enters the container (the Workspace Manager strips it).
        string? workspaceRoot = null;
        var repositories = (schema?.Repositories ?? []).ToList();
        foreach (var mount in command.WorkspaceMounts ?? [])
        {
            if (!mount.SettingsByRole.TryGetValue(WorkspaceMountRoles.CloneUrl, out var cloneUrl)
                || string.IsNullOrWhiteSpace(cloneUrl))
            {
                await FailPreFlightAsync(instanceId, workflowType,
                    $"workspace mount '{mount.MountId}' ({mount.ProviderType}) declares no clone source", ct);
                return;
            }
            if (mount.AuthSlotName is { } authSlotName)
            {
                if (string.IsNullOrEmpty(command.ResolutionToken))
                {
                    await FailPreFlightAsync(instanceId, workflowType,
                        $"workspace mount '{mount.MountId}' requires authentication but the run carries no resolution token", ct);
                    return;
                }
                var auth = await repositoryAuthResolver.ResolveAsync(
                    command.CommandId, command.ResolutionToken, authSlotName, ct);
                if (auth is null)
                {
                    await FailPreFlightAsync(instanceId, workflowType,
                        $"could not resolve the credential for workspace mount '{mount.MountId}'", ct);
                    return;
                }
                cloneUrl = RepositoryCloneUrl.WithCredentials(cloneUrl, auth.Username, auth.Token);
            }
            var branch = mount.SettingsByRole.GetValueOrDefault(WorkspaceMountRoles.Branch);
            var noCache = string.Equals(
                mount.SettingsByRole.GetValueOrDefault(WorkspaceMountRoles.NoCache),
                "true", StringComparison.OrdinalIgnoreCase);
            var allowPush = string.Equals(
                mount.SettingsByRole.GetValueOrDefault(WorkspaceMountRoles.AllowPush),
                "true", StringComparison.OrdinalIgnoreCase);
            repositories.Add(new RepositoryDeclaration(
                mount.MountId, cloneUrl,
                string.IsNullOrWhiteSpace(branch) ? null : branch, noCache)
            {
                AllowPush = allowPush,
                CommitName = mount.SettingsByRole.GetValueOrDefault(WorkspaceMountRoles.CommitName),
                CommitEmail = mount.SettingsByRole.GetValueOrDefault(WorkspaceMountRoles.CommitEmail),
            });

            // Each mount's effective root (clone + optional working directory) is announced to the
            // container generically; workflows resolve their mounts from these variables.
            var workingDirectory = mount.SettingsByRole.GetValueOrDefault(WorkspaceMountRoles.WorkingDirectory);
            var mountRoot = $"/workspace/repos/{mount.MountId}";
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                mountRoot = $"{mountRoot}/{workingDirectory.Trim('/', '\\')}";
            env[$"{WorkflowEnvironmentVariables.WorkspaceMountPrefix}{mount.MountId.ToUpperInvariant()}"] = mountRoot;
        }
        if (repositories.Count > 0)
        {
            try
            {
                await workspaceManager.PrepareAsync(instanceId, repositories, ct);
                workspaceRoot = ResolveWorkspaceDirectoryBind(dispatcherSettings.Value, instanceId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Workspace preparation failed. InstanceId={InstanceId} WorkflowType={WorkflowType}",
                    instanceId, workflowType);
                await FailPreFlightAsync(instanceId, workflowType,
                    $"workspace preparation failed: {ex.Message}", ct);
                return;
            }

            env[WorkflowEnvironmentVariables.WorkspaceDirectory] = "/workspace";
            await auditLog.AppendAsync(
                "steering-instance", "workflow.workspace-prepared",
                instanceId.ToString(), repositories.Count.ToString(), ct: ct);
        }

        // Workflows declaring an interactive web terminal get its container named so the
        // backend can reach the terminal by name on the shared network. A declared gate makes
        // the terminal per-run — evaluated as data against the run's context.
        var terminalPort = InteractiveTerminalResolver.ResolvePort(schema, command.Context);
        var terminalContainerName = terminalPort is null ? null : $"auxilia-session-{instanceId:N}";

        // A crashed container must fail its run visibly — never leave it stuck in Queued/Running.
        Func<ContainerExit, Task> onContainerExited =
            exit => HandleContainerExitAsync(instanceId, workflowType, exit);

        // docker:// URI — skip download/verify/extract; use baked image
        if (packageUri.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            var imageName = packageUri.Substring("docker://".Length);
            WorkflowLaunchResult launched;
            try
            {
                launched = await launcher.LaunchAsync(
                    new WorkflowLaunchRequest(string.Empty, env, pluginFiles)
                    {
                        DockerImageUri = imageName,
                        EnvironmentLayers = environmentLayers.Count > 0 ? environmentLayers : null,
                        OutputDirectoryBind = outputDirectoryBind,
                        NetworkPolicy = networkPolicy,
                        WorkspaceDirectoryBind = workspaceRoot,
                        PublishTerminalPort = terminalPort,
                        TerminalContainerName = terminalContainerName,
                        OnExited = onContainerExited
                    },
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A launch that never started a container (plugin/image incompatibility, a
                // failed environment-image build, Docker down) must FAIL the run visibly —
                // never leave it stranded in Received.
                logger.LogError(ex,
                    "Workflow launch failed pre-start. CommandId={CommandId} WorkflowType={WorkflowType}",
                    command.CommandId, workflowType);
                await FailPreFlightAsync(instanceId, workflowType, $"launch failed: {ex.Message}", ct);
                return;
            }
            await StampTerminalEndpointAsync(instanceId, launched, ct);
            await MarkQueuedAsync(instanceId, workflowType, packageUri, launched.TerminalEndpoint, ct);
            return;
        }

        // 1. Download the package
        var http = httpClientFactory.CreateClient("workflow-packages");
        byte[] packageBytes;
        try
        {
            packageBytes = await http.GetByteArrayAsync(packageUri, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to download workflow package from {PackageUri}.", packageUri);
            await FailPreFlightAsync(instanceId, workflowType,
                "workflow package download failed", ct);
            return;
        }

        // 2. Verify the package
        if (!packageVerifier.Verify(new MemoryStream(packageBytes)))
        {
            logger.LogError(
                "Workflow package verification failed for {WorkflowType}. Aborting launch.",
                workflowType);
            await FailPreFlightAsync(instanceId, workflowType,
                "workflow package signature verification failed", ct);
            return;
        }

        // 3. Extract to a temp directory
        var extractedPath = Path.Combine(Path.GetTempPath(), $"auxilia-wf-{Guid.NewGuid()}");
        Directory.CreateDirectory(extractedPath);
        using var archive = new ZipArchive(new MemoryStream(packageBytes), ZipArchiveMode.Read);
        archive.ExtractToDirectory(extractedPath);

        logger.LogInformation(
            "Workflow package extracted. WorkflowType={WorkflowType} Path={Path}",
            workflowType, extractedPath);

        // 4. Register for the announcement handler
        pendingPackages.Store(workflowType, extractedPath);

        // 6. Launch
        var launchResult = await launcher.LaunchAsync(new WorkflowLaunchRequest(extractedPath, env, pluginFiles)
        {
            OutputDirectoryBind = outputDirectoryBind,
            NetworkPolicy = networkPolicy,
            WorkspaceDirectoryBind = workspaceRoot,
            PublishTerminalPort = terminalPort,
            TerminalContainerName = terminalContainerName,
            OnExited = onContainerExited
        }, ct);
        await StampTerminalEndpointAsync(instanceId, launchResult, ct);
        await MarkQueuedAsync(instanceId, workflowType, packageUri, launchResult.TerminalEndpoint, ct);
    }

    /// <summary>The dashboard proxies this endpoint — authenticated — to the run's owner.</summary>
    private async Task StampTerminalEndpointAsync(
        Guid instanceId, WorkflowLaunchResult launched, CancellationToken ct)
    {
        if (launched.TerminalEndpoint is { Length: > 0 } endpoint)
            await instanceRegistry.SetTerminalEndpointAsync(instanceId, endpoint, ct);
    }

    /// <summary>
    /// Selects the run-output bind source handed to the launcher. The Docker daemon resolves
    /// bind sources on the HOST, so when the Core.Runner runs in a container (its
    /// RunOutputDirectory being a container-local mount of a host directory), the launcher
    /// must receive the host view ({RunOutputHostDirectory}/{id}) of the directory the
    /// dispatcher created as {RunOutputDirectory}/{id}. The host path's separator style is
    /// preserved as-is (e.g. <c>C:\…</c> administered from a Linux container) because the
    /// daemon interprets it, not this process's OS.
    /// </summary>
    internal static string ResolveOutputDirectoryBind(WorkflowDispatcherSettings settings, Guid instanceId)
    {
        if (string.IsNullOrWhiteSpace(settings.RunOutputHostDirectory))
            return Path.Combine(settings.RunOutputDirectory, instanceId.ToString("N"));

        var root = settings.RunOutputHostDirectory.TrimEnd('/', '\\');
        var separator = root.Contains('\\') ? '\\' : '/';
        return $"{root}{separator}{instanceId:N}";
    }

    /// <summary>Workspace bind source for the launcher — same host-view rule as the output bind.</summary>
    internal static string ResolveWorkspaceDirectoryBind(WorkflowDispatcherSettings settings, Guid instanceId)
    {
        if (string.IsNullOrWhiteSpace(settings.WorkspaceRootHostDirectory))
            return Path.Combine(settings.WorkspaceRootDirectory, instanceId.ToString("N"));

        var root = settings.WorkspaceRootHostDirectory.TrimEnd('/', '\\');
        var separator = root.Contains('\\') ? '\\' : '/';
        return $"{root}{separator}{instanceId:N}";
    }

    /// <summary>
    /// The container exit watcher's report: after a short grace (in-flight completion events may
    /// still land), a run whose state is not terminal is failed with the exit code and log tail.
    /// A normal exit (the workflow reported Success/Failed/Cancelled over the bus, or a
    /// long-living drain) changes nothing.
    /// </summary>
    internal async Task HandleContainerExitAsync(Guid instanceId, string workflowType, ContainerExit exit)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(dispatcherSettings.Value.ContainerExitGraceSeconds));

            var record = await instanceRegistry.GetAsync(instanceId);
            if (record?.State is null or "Success" or "Failed" or "Cancelled" or "PreFlightFailed" or "Draining")
                return;

            var reason = $"workflow container exited (code {exit.ExitCode}) before completing"
                + (string.IsNullOrWhiteSpace(exit.LogTail)
                    ? " — the container produced no output"
                    : $" — last output: {Truncate(exit.LogTail, 2000)}");
            logger.LogError(
                "Workflow {InstanceId} container died without a terminal state. ExitCode={ExitCode} LastState={State}",
                instanceId, exit.ExitCode, record.State);

            await instanceRegistry.SetStateAsync(instanceId, "Failed", reason);
            await statusPublisher.PublishAsync(instanceId, workflowType, "Failed", reason);
            tokenRegistry.Consume(instanceId);
            await auditLog.AppendAsync(
                "core-runner", "workflow.container-exit", instanceId.ToString(), "failed", reason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Handling the container exit of {InstanceId} failed.", instanceId);
        }
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    private async Task FailPreFlightAsync(Guid instanceId, string workflowType, string reason, CancellationToken ct)
    {
        await instanceRegistry.SetStateAsync(instanceId, "PreFlightFailed", reason, ct);
        await statusPublisher.PublishAsync(instanceId, workflowType, "PreFlightFailed", reason, ct: ct);
        tokenRegistry.Consume(instanceId);
    }

    private async Task MarkQueuedAsync(
        Guid instanceId, string workflowType, string packageUri, string? terminalEndpoint, CancellationToken ct)
    {
        // Every successfully dispatched package is known to the registry from then on, so the
        // configuration editor can offer it without a separate deployment registration.
        await packageStore.LearnAsync(workflowType, packageUri, ct);
        await instanceRegistry.SetStateAsync(instanceId, "Queued", ct: ct);
        // The terminal endpoint (Core-reachable, per launch) rides the Queued event; the Core
        // preserves it across later transitions.
        await statusPublisher.PublishAsync(
            instanceId, workflowType, "Queued", terminalEndpoint: terminalEndpoint, ct: ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}


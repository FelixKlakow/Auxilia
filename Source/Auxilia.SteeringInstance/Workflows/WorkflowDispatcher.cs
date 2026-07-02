using System.IO.Compression;
using System.Text.Json;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

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
    SlotConfigurationStore slotStore,
    SlotProviderRegistry providerRegistry,
    WorkflowConfigurationStore configurationStore,
    WorkflowInstanceTokenRegistry tokenRegistry,
    IPolicyEngine policyEngine,
    WorkflowInstanceRegistry instanceRegistry,
    WorkflowStatusPublisher statusPublisher,
    WorkflowSchemaStore schemaStore,
    WorkflowPackageStore packageStore,
    Auxilia.PlatformData.Artifacts.IArtifactStore artifactStore,
    NetworkPolicyResolver networkPolicyResolver,
    WorkspaceManager workspaceManager,
    AuditLog auditLog,
    SteeringInstanceInfo instanceInfo,
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
        // Named-configuration dispatch (#18): the configuration supplies workflow type and
        // package URI when the command omits them. A missing or disabled configuration still
        // creates the instance record first so the pre-flight failure is visible everywhere.
        StoredWorkflowConfiguration? configuration = null;
        string? configurationError = null;
        if (command.WorkflowConfigurationId is { } configurationId)
        {
            configuration = await configurationStore.GetAsync(configurationId, ct);
            if (configuration is null)
                configurationError = $"workflow configuration '{configurationId}' not found";
            else if (!configuration.Enabled)
                configurationError = $"workflow configuration '{configuration.Name}' is disabled";
        }

        var workflowType = configuration?.WorkflowType ?? command.WorkflowType ?? string.Empty;
        var packageUri = configuration?.PackageUri ?? command.WorkflowPackageUri ?? string.Empty;

        logger.LogInformation(
            "Received RunWorkflowCommand. CommandId={CommandId} WorkflowType={WorkflowType} PackageUri={PackageUri}",
            command.CommandId, workflowType, packageUri);

        // The instance identity exists for the whole lifecycle — including pre-flight
        // rejections — so every outcome is visible in the dashboard.
        var issued = tokenRegistry.Issue(workflowType);
        var instanceId = issued.WorkflowInstanceId;
        await instanceRegistry.CreateAsync(
            instanceId, workflowType, "Received",
            instanceInfo.ServiceId, JsonSerializer.Serialize(command),
            configuration?.Id, configuration?.Name, ct);
        await statusPublisher.PublishAsync(instanceId, workflowType, "Received", ct: ct);

        if (configurationError is not null)
        {
            logger.LogWarning(
                "Dispatch rejected: {Reason}. CommandId={CommandId}",
                configurationError, command.CommandId);
            await auditLog.AppendAsync(
                "steering-instance", "workflow.dispatch.rejected",
                instanceId.ToString(), configurationError, ct: ct);
            await FailPreFlightAsync(instanceId, workflowType, configurationError, ct);
            return;
        }

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

        // 5b. Resolve slot plugin files. A named configuration fully defines the run's slot
        // bindings; an unregistered provider type fails pre-flight there. The configuration-less
        // path stays byte-for-byte: providers come from the global (type, slot) table and
        // missing registrations are merely skipped.
        var pluginFiles = new List<SlotPluginFile>();
        if (configuration is not null)
        {
            foreach (var providerType in configuration.SlotBindings.Select(b => b.ProviderType).Distinct())
            {
                var dllPath = await providerRegistry.GetDllPathAsync(providerType, ct);
                if (dllPath is null)
                {
                    var reason =
                        $"workflow configuration '{configuration.Name}' references unregistered slot provider '{providerType}'";
                    logger.LogWarning(
                        "Dispatch rejected: {Reason}. CommandId={CommandId}", reason, command.CommandId);
                    await auditLog.AppendAsync(
                        "steering-instance", "workflow.dispatch.rejected",
                        instanceId.ToString(), reason, ct: ct);
                    await FailPreFlightAsync(instanceId, workflowType, reason, ct);
                    return;
                }

                var manifestPath = Path.ChangeExtension(dllPath, null) + ".manifest.json";
                pluginFiles.Add(new SlotPluginFile(dllPath, manifestPath));
            }
        }
        else
        {
            var providerTypes = (await slotStore.GetConfigurationsAsync(workflowType, ct))
                .Select(c => c.ProviderType)
                .Distinct();

            foreach (var providerType in providerTypes)
            {
                var dllPath = await providerRegistry.GetDllPathAsync(providerType, ct);
                if (dllPath is null)
                {
                    logger.LogWarning(
                        "No SlotPackages entry for ProviderType={ProviderType} (WorkflowType={WorkflowType}). Skipping.",
                        providerType, workflowType);
                    continue;
                }

                var manifestPath = Path.ChangeExtension(dllPath, null) + ".manifest.json";
                pluginFiles.Add(new SlotPluginFile(dllPath, manifestPath));
            }
        }

        if (pluginFiles.Count > 0)
            logger.LogInformation(
                "Resolved {Count} slot plugin file(s) for {WorkflowType}: {ProviderTypes}",
                pluginFiles.Count, workflowType,
                string.Join(", ", pluginFiles.Select(f => f.DllPath)));

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

        // Per-run repository workspace (ARCHITECTURE §9): declared repos from the stored
        // schema are prepared by the Workspace Manager and bind-mounted at /workspace.
        string? workspaceRoot = null;
        var repositories = schema?.Repositories ?? [];
        if (repositories.Count > 0)
        {
            try
            {
                workspaceRoot = await workspaceManager.PrepareAsync(instanceId, repositories, ct);
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

        // Workflows declaring an interactive web terminal get its port published at launch.
        var terminalPort = (await schemaStore.GetSchemaAsync(workflowType, ct))?.InteractiveTerminalPort;

        // docker:// URI — skip download/verify/extract; use baked image
        if (packageUri.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            var imageName = packageUri.Substring("docker://".Length);
            var launched = await launcher.LaunchAsync(
                new WorkflowLaunchRequest(string.Empty, env, pluginFiles)
                {
                    DockerImageUri = imageName,
                    OutputDirectoryBind = outputDirectoryBind,
                    NetworkPolicy = networkPolicy,
                    WorkspaceDirectoryBind = workspaceRoot,
                    PublishTerminalPort = terminalPort
                },
                ct);
            await MarkQueuedAsync(instanceId, workflowType, packageUri, ct);
            await StampTerminalEndpointAsync(instanceId, launched, ct);
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
            PublishTerminalPort = terminalPort
        }, ct);
        await MarkQueuedAsync(instanceId, workflowType, packageUri, ct);
        await StampTerminalEndpointAsync(instanceId, launchResult, ct);
    }

    /// <summary>The dashboard proxies this endpoint — authenticated — to the run's owner.</summary>
    private async Task StampTerminalEndpointAsync(
        Guid instanceId, WorkflowLaunchResult launched, CancellationToken ct)
    {
        if (launched.TerminalHostPort is { } hostPort)
            await instanceRegistry.SetTerminalEndpointAsync(
                instanceId, $"{launcherSettings.Value.TerminalPublishHost}:{hostPort}", ct);
    }

    /// <summary>
    /// Selects the run-output bind source handed to the launcher. The Docker daemon resolves
    /// bind sources on the HOST, so when the Steering Instance runs in a container (its
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

    private async Task FailPreFlightAsync(Guid instanceId, string workflowType, string reason, CancellationToken ct)
    {
        await instanceRegistry.SetStateAsync(instanceId, "PreFlightFailed", reason, ct);
        await statusPublisher.PublishAsync(instanceId, workflowType, "PreFlightFailed", reason, ct);
        tokenRegistry.Consume(instanceId);
    }

    private async Task MarkQueuedAsync(Guid instanceId, string workflowType, string packageUri, CancellationToken ct)
    {
        // Every successfully dispatched package is known to the registry from then on, so the
        // configuration editor can offer it without a separate deployment registration.
        await packageStore.LearnAsync(workflowType, packageUri, ct);
        await instanceRegistry.SetStateAsync(instanceId, "Queued", ct: ct);
        await statusPublisher.PublishAsync(instanceId, workflowType, "Queued", ct: ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}


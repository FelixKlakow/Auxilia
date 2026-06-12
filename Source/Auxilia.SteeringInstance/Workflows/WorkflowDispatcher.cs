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
    WorkflowInstanceTokenRegistry tokenRegistry,
    IPolicyEngine policyEngine,
    WorkflowInstanceRegistry instanceRegistry,
    WorkflowStatusPublisher statusPublisher,
    WorkflowSchemaStore schemaStore,
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
        logger.LogInformation(
            "Received RunWorkflowCommand. CommandId={CommandId} WorkflowType={WorkflowType} PackageUri={PackageUri}",
            command.CommandId, command.WorkflowType, command.WorkflowPackageUri);

        // The instance identity exists for the whole lifecycle — including pre-flight
        // rejections — so every outcome is visible in the dashboard.
        var issued = tokenRegistry.Issue(command.WorkflowType);
        var instanceId = issued.WorkflowInstanceId;
        await instanceRegistry.CreateAsync(
            instanceId, command.WorkflowType, "Received",
            instanceInfo.ServiceId, JsonSerializer.Serialize(command), ct);
        await statusPublisher.PublishAsync(instanceId, command.WorkflowType, "Received", ct: ct);

        // Pre-flight authorization: the trigger permission of the requesting principal.
        if (command.RequestedBy is { } principalId)
        {
            var decision = await policyEngine.EvaluateAsync(
                new PolicyContext(principalId, PermissionActions.WorkflowTrigger, command.CommandId.ToString())
                {
                    WorkflowType = command.WorkflowType
                }, ct);
            if (!decision.Allowed)
            {
                logger.LogWarning(
                    "Dispatch denied by policy. CommandId={CommandId} WorkflowType={WorkflowType} Principal={Principal} Reason={Reason}",
                    command.CommandId, command.WorkflowType, principalId, decision.Reason);
                await FailPreFlightAsync(instanceId, command.WorkflowType,
                    $"dispatch denied by policy: {decision.Reason}", ct);
                return;
            }
        }
        else if (dispatcherSettings.Value.RequirePrincipal)
        {
            logger.LogWarning(
                "Dispatch rejected: RunWorkflowCommand without RequestedBy principal while RequirePrincipal is enabled. CommandId={CommandId}",
                command.CommandId);
            await FailPreFlightAsync(instanceId, command.WorkflowType,
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

        // Merge extra environment variables (e.g. AUXILIA_DEVELOPER_MODE)
        if (settings.ExtraEnvironmentVariables is not null)
            foreach (var (key, value) in settings.ExtraEnvironmentVariables)
                env[key] = value;

        // 5b. Resolve slot plugin files
        var pluginFiles = new List<SlotPluginFile>();
        var providerTypes = (await slotStore.GetConfigurationsAsync(command.WorkflowType, ct))
            .Select(c => c.ProviderType)
            .Distinct();

        foreach (var providerType in providerTypes)
        {
            var dllPath = await providerRegistry.GetDllPathAsync(providerType, ct);
            if (dllPath is null)
            {
                logger.LogWarning(
                    "No SlotPackages entry for ProviderType={ProviderType} (WorkflowType={WorkflowType}). Skipping.",
                    providerType, command.WorkflowType);
                continue;
            }

            var manifestPath = Path.ChangeExtension(dllPath, null) + ".manifest.json";
            pluginFiles.Add(new SlotPluginFile(dllPath, manifestPath));
        }

        if (pluginFiles.Count > 0)
            logger.LogInformation(
                "Resolved {Count} slot plugin file(s) for {WorkflowType}: {ProviderTypes}",
                pluginFiles.Count, command.WorkflowType,
                string.Join(", ", pluginFiles.Select(f => f.DllPath)));

        // Effective network policy (ARCHITECTURE §10): manifest baseline (last stored schema)
        // merged with run-configuration extras, clamped by platform policy, audited per run.
        var schema = await schemaStore.GetSchemaAsync(command.WorkflowType, ct);
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
                    instanceId, command.WorkflowType);
                await FailPreFlightAsync(instanceId, command.WorkflowType,
                    $"workspace preparation failed: {ex.Message}", ct);
                return;
            }

            env[WorkflowEnvironmentVariables.WorkspaceDirectory] = "/workspace";
            await auditLog.AppendAsync(
                "steering-instance", "workflow.workspace-prepared",
                instanceId.ToString(), repositories.Count.ToString(), ct: ct);
        }

        // docker:// URI — skip download/verify/extract; use baked image
        if (command.WorkflowPackageUri.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            var imageName = command.WorkflowPackageUri.Substring("docker://".Length);
            await launcher.LaunchAsync(
                new WorkflowLaunchRequest(string.Empty, env, pluginFiles)
                {
                    DockerImageUri = imageName,
                    OutputDirectoryBind = outputDirectoryBind,
                    NetworkPolicy = networkPolicy,
                    WorkspaceDirectoryBind = workspaceRoot
                },
                ct);
            await MarkQueuedAsync(instanceId, command.WorkflowType, ct);
            return;
        }

        // 1. Download the package
        var http = httpClientFactory.CreateClient("workflow-packages");
        byte[] packageBytes;
        try
        {
            packageBytes = await http.GetByteArrayAsync(command.WorkflowPackageUri, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to download workflow package from {PackageUri}.", command.WorkflowPackageUri);
            await FailPreFlightAsync(instanceId, command.WorkflowType,
                "workflow package download failed", ct);
            return;
        }

        // 2. Verify the package
        if (!packageVerifier.Verify(new MemoryStream(packageBytes)))
        {
            logger.LogError(
                "Workflow package verification failed for {WorkflowType}. Aborting launch.",
                command.WorkflowType);
            await FailPreFlightAsync(instanceId, command.WorkflowType,
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
            command.WorkflowType, extractedPath);

        // 4. Register for the announcement handler
        pendingPackages.Store(command.WorkflowType, extractedPath);

        // 6. Launch
        await launcher.LaunchAsync(new WorkflowLaunchRequest(extractedPath, env, pluginFiles)
        {
            OutputDirectoryBind = outputDirectoryBind,
            NetworkPolicy = networkPolicy,
            WorkspaceDirectoryBind = workspaceRoot
        }, ct);
        await MarkQueuedAsync(instanceId, command.WorkflowType, ct);
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

    private async Task MarkQueuedAsync(Guid instanceId, string workflowType, CancellationToken ct)
    {
        await instanceRegistry.SetStateAsync(instanceId, "Queued", ct: ct);
        await statusPublisher.PublishAsync(instanceId, workflowType, "Queued", ct: ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}


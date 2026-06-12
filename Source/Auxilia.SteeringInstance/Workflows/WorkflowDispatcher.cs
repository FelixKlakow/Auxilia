using System.IO.Compression;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
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
                return;
            }
        }
        else if (dispatcherSettings.Value.RequirePrincipal)
        {
            logger.LogWarning(
                "Dispatch rejected: RunWorkflowCommand without RequestedBy principal while RequirePrincipal is enabled. CommandId={CommandId}",
                command.CommandId);
            return;
        }

        var settings = launcherSettings.Value;

        // Issue the per-launch identity and one-time token, and pre-create the instance's
        // exclusive response queue so configuration is never delivered to a self-declared topic.
        var issued = tokenRegistry.Issue(command.WorkflowType);
        await messageBus.DeclareQueueAsync(WorkflowQueues.ResponseQueueFor(issued.WorkflowInstanceId), ct);

        // 5. Build env vars
        var env = new Dictionary<string, string>
        {
            ["RabbitMq__Host"]               = settings.RabbitMqHost,
            ["RabbitMq__Port"]               = settings.RabbitMqPort.ToString(),
            ["RabbitMq__UserName"]           = settings.RabbitMqUserName,
            ["RabbitMq__Password"]           = settings.RabbitMqPassword,
            [WorkflowEnvironmentVariables.RegistrationQueue] = dispatcherSettings.Value.RegistrationQueueName,
            [WorkflowEnvironmentVariables.AnnouncementQueue] = dispatcherSettings.Value.AnnouncementQueueName,
            [WorkflowEnvironmentVariables.InstanceId]        = issued.WorkflowInstanceId.ToString("D"),
            [WorkflowEnvironmentVariables.InstanceToken]     = issued.Token,
        };

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

        // docker:// URI — skip download/verify/extract; use baked image
        if (command.WorkflowPackageUri.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            var imageName = command.WorkflowPackageUri.Substring("docker://".Length);
            await launcher.LaunchAsync(
                new WorkflowLaunchRequest(string.Empty, env, pluginFiles) { DockerImageUri = imageName },
                ct);
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
            return;
        }

        // 2. Verify the package
        if (!packageVerifier.Verify(new MemoryStream(packageBytes)))
        {
            logger.LogError(
                "Workflow package verification failed for {WorkflowType}. Aborting launch.",
                command.WorkflowType);
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
        await launcher.LaunchAsync(new WorkflowLaunchRequest(extractedPath, env, pluginFiles), ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}


using System.IO.Compression;
using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows.Crypto;
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
    IHttpClientFactory httpClientFactory,
    IWorkflowPackageVerifier packageVerifier,
    PendingWorkflowPackageStore pendingPackages,
    ILogger<WorkflowDispatcher> logger)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await messageBus.DeclareQueueAsync("workflow.run-commands", ct);
        _subscription = await messageBus.SubscribeAsync<RunWorkflowCommand>(
            "workflow.run-commands", HandleAsync, ct);

        logger.LogInformation("WorkflowDispatcher started — listening on workflow.run-commands.");
    }

    private async Task HandleAsync(RunWorkflowCommand command, CancellationToken ct)
    {
        logger.LogInformation(
            "Received RunWorkflowCommand. CommandId={CommandId} WorkflowType={WorkflowType} PackageUri={PackageUri}",
            command.CommandId, command.WorkflowType, command.WorkflowPackageUri);

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

        // 5. Build env vars
        var settings = launcherSettings.Value;
        var env = new Dictionary<string, string>
        {
            ["RabbitMq__Host"]     = settings.RabbitMqHost,
            ["RabbitMq__Port"]     = settings.RabbitMqPort.ToString(),
            ["RabbitMq__UserName"] = settings.RabbitMqUserName,
            ["RabbitMq__Password"] = settings.RabbitMqPassword,
        };

        foreach (var (key, value) in command.Context)
            env[$"WORKFLOW_CONTEXT__{key.ToUpperInvariant()}"] = value;

        // 6. Launch
        await launcher.LaunchAsync(new WorkflowLaunchRequest(extractedPath, env), ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}


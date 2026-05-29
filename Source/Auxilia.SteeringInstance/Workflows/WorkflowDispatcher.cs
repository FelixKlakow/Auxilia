using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Subscribes to the <c>workflow.run-commands</c> queue. For each
/// <see cref="RunWorkflowCommand"/> it builds a <see cref="WorkflowLaunchRequest"/>
/// (injecting RabbitMQ connection details and the command's context as environment
/// variables) and delegates to <see cref="IWorkflowLauncher"/>.
///
/// The dispatcher does not wait for the workflow to finish — lifecycle tracking is
/// handled downstream by <see cref="WorkflowRegistrationHandler"/> and
/// <see cref="WorkflowAnnouncementHandler"/>.
/// </summary>
public sealed class WorkflowDispatcher(
    IMessageBusClient messageBus,
    IWorkflowLauncher launcher,
    IOptions<DockerWorkflowLauncherSettings> launcherSettings,
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
            "Received RunWorkflowCommand. CommandId={CommandId} WorkflowType={WorkflowType} Image={Image}",
            command.CommandId, command.WorkflowType, command.WorkflowImage);

        var settings = launcherSettings.Value;

        var env = new Dictionary<string, string>
        {
            ["RabbitMq__Host"]     = settings.RabbitMqHost,
            ["RabbitMq__Port"]     = settings.RabbitMqPort.ToString(),
            ["RabbitMq__UserName"] = settings.RabbitMqUserName,
            ["RabbitMq__Password"] = settings.RabbitMqPassword,
        };

        // Forward caller-supplied context as WORKFLOW_CONTEXT__<KEY> env vars so
        // the workflow binary can read them without coupling to a specific message shape.
        foreach (var (key, value) in command.Context)
            env[$"WORKFLOW_CONTEXT__{key.ToUpperInvariant()}"] = value;

        await launcher.LaunchAsync(new WorkflowLaunchRequest(command.WorkflowImage, env), ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}



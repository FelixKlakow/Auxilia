using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Subscribes to the <c>workflow.announcements</c> queue and responds to each
/// <see cref="WorkflowAnnouncementMessage"/> with a <see cref="WorkflowDirective"/>
/// instructing the workflow to run.
///
/// Before sending the <see cref="WorkflowDirectiveKind.Run"/> directive the handler
/// pre-loads the workflow schema from the pending ZIP package (if one was registered by
/// the dispatcher) so that the schema store is populated before the workflow starts executing.
/// </summary>
public sealed class WorkflowAnnouncementHandler(
    IMessageBusClient messageBus,
    ILogger<WorkflowAnnouncementHandler> logger,
    WorkflowSchemaStore schemaStore,
    PendingWorkflowPackageStore pendingPackages)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await messageBus.DeclareQueueAsync("workflow.announcements", ct);
        _subscription = await messageBus.SubscribeAsync<WorkflowAnnouncementMessage>(
            "workflow.announcements", HandleAsync, ct);

        logger.LogInformation("WorkflowAnnouncementHandler started — listening on workflow.announcements.");
    }

    private async Task HandleAsync(WorkflowAnnouncementMessage message, CancellationToken ct)
    {
        logger.LogInformation(
            "Received WorkflowAnnouncement: Workflow={WorkflowName} InstanceId={InstanceId}. Sending Run directive.",
            message.WorkflowName, message.WorkflowInstanceId);

        if (pendingPackages.TryConsume(message.WorkflowName, out var extractedPath))
        {
            var schemaJson = await File.ReadAllTextAsync(
                Path.Combine(extractedPath, "workflow-schema.json"), ct);
            var schema = JsonSerializer.Deserialize<WorkflowSchema>(schemaJson);
            if (schema is not null)
                schemaStore.SetSchema(message.WorkflowName, schema);
        }
        else
        {
            logger.LogWarning(
                "No pending package for {WorkflowName}; schema not pre-loaded.",
                message.WorkflowName);
        }

        await messageBus.PublishAsync(
            message.ResponseTopic,
            new WorkflowDirective(message.WorkflowInstanceId, WorkflowDirectiveKind.Run),
            ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}



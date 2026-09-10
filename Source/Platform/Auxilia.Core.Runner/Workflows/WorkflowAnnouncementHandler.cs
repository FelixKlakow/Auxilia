using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

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
    PendingWorkflowPackageStore pendingPackages,
    WorkflowInstanceTokenRegistry tokenRegistry,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var queueName = dispatcherSettings.Value.AnnouncementQueueName;
        await messageBus.DeclareQueueAsync(queueName, ct);
        _subscription = await messageBus.SubscribeAsync<WorkflowAnnouncementMessage>(
            queueName, HandleAsync, ct);

        logger.LogInformation("WorkflowAnnouncementHandler started — listening on {QueueName}.", queueName);
    }

    private async Task HandleAsync(WorkflowAnnouncementMessage message, CancellationToken ct)
    {
        var responseTopic = message.ResponseTopic;
        if (dispatcherSettings.Value.RequireInstanceToken)
        {
            if (!tokenRegistry.Validate(message.WorkflowInstanceId, message.InstanceToken))
            {
                logger.LogWarning(
                    "Rejected WorkflowAnnouncement with missing or invalid instance token. Workflow={WorkflowName} InstanceId={InstanceId}",
                    message.WorkflowName, message.WorkflowInstanceId);
                return;
            }

            // The announced name selects the pending package + schema: it must be the type
            // the token was issued for.
            if (!tokenRegistry.Validate(message.WorkflowInstanceId, message.InstanceToken, message.WorkflowName))
            {
                logger.LogWarning(
                    "Rejected WorkflowAnnouncement: announced {WorkflowName} but the instance token was issued for another type. InstanceId={InstanceId}",
                    message.WorkflowName, message.WorkflowInstanceId);
                return;
            }

            // Authenticated instances are only ever answered on the queue the platform
            // pre-created at launch — the self-declared ResponseTopic is ignored.
            responseTopic = WorkflowQueues.ResponseQueueFor(message.WorkflowInstanceId);
        }

        logger.LogInformation(
            "Received WorkflowAnnouncement: Workflow={WorkflowName} InstanceId={InstanceId}. Sending Run directive.",
            message.WorkflowName, message.WorkflowInstanceId);

        if (pendingPackages.TryConsume(message.WorkflowInstanceId, out var extractedPath))
        {
            var schemaJson = await File.ReadAllTextAsync(
                Path.Combine(extractedPath, "workflow-schema.json"), ct);
            var schema = JsonSerializer.Deserialize<WorkflowSchema>(schemaJson);
            if (schema is not null)
                await schemaStore.SetSchemaAsync(message.WorkflowName, schema, ct);
        }
        else
        {
            logger.LogWarning(
                "No pending package for {WorkflowName}; schema not pre-loaded.",
                message.WorkflowName);
        }

        await messageBus.PublishAsync(
            responseTopic,
            new WorkflowDirective(message.WorkflowInstanceId, WorkflowDirectiveKind.Run),
            ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}



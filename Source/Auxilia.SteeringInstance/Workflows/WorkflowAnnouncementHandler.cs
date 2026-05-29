using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Subscribes to the <c>workflow.announcements</c> queue and responds to each
/// <see cref="WorkflowAnnouncementMessage"/> with a <see cref="WorkflowDirective"/>
/// instructing the workflow to run.
///
/// In v1 every announcement unconditionally receives a <see cref="WorkflowDirectiveKind.Run"/>
/// directive. Future iterations can inspect the manifest or dispatch state to issue
/// <see cref="WorkflowDirectiveKind.EmitSchema"/> when a schema refresh is needed instead.
/// </summary>
public sealed class WorkflowAnnouncementHandler(
    IMessageBusClient messageBus,
    ILogger<WorkflowAnnouncementHandler> logger)
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



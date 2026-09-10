using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Forwards every <see cref="CancelWorkflowCommand"/> from the shared <c>workflow.cancel-commands</c>
/// queue to the instance's own <c>workflow-cancel-{id}</c> queue — UNCONDITIONALLY. The shared queue
/// hands each cancel to ONE runner of the pool, which need not own the instance; forwarding by id
/// (instead of dropping "unknown" instances) makes the per-instance queue the single meeting
/// point, where the owning container's SDK consumes it.
/// </summary>
public sealed class WorkflowCancelDispatcher(
    IMessageBusClient messageBus,
    ILogger<WorkflowCancelDispatcher> logger)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await messageBus.DeclareQueueAsync("workflow.cancel-commands", ct);
        _subscription = await messageBus.SubscribeAsync<CancelWorkflowCommand>(
            "workflow.cancel-commands", HandleAsync, ct);

        logger.LogInformation("WorkflowCancelDispatcher started — listening on workflow.cancel-commands.");
    }

    private async Task HandleAsync(CancelWorkflowCommand command, CancellationToken ct)
    {
        var topic = $"workflow-cancel-{command.WorkflowInstanceId}";
        // Declare-before-publish: a cancel racing the workflow's startup must PARK on the queue
        // until the instance subscribes — published unrouted it is silently dropped, and a
        // "hard stop" that can vanish is no hard stop (caught by the client-surface system test).
        await messageBus.DeclareQueueAsync(topic, ct);
        await messageBus.PublishAsync(topic, command, ct);

        logger.LogInformation(
            "Forwarded cancel command to {Topic} for instance {InstanceId}.",
            topic, command.WorkflowInstanceId);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

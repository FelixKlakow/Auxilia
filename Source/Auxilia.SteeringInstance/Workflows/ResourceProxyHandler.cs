using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Audited Resource Proxy (ARCHITECTURE §8): workflows publish <see cref="ResourceRequest"/>s
/// authenticated by the instance token; the handler routes them to the registered
/// <see cref="IResourceConnector"/> and answers only on the instance's canonical response
/// queue. Every call is audited; the workflow never holds resource credentials.
/// </summary>
public sealed class ResourceProxyHandler(
    IMessageBusClient messageBus,
    IEnumerable<IResourceConnector> connectors,
    WorkflowInstanceTokenRegistry tokenRegistry,
    AuditLog auditLog,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
    ILogger<ResourceProxyHandler> logger)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var queueName = dispatcherSettings.Value.ResourceProxyQueueName;
        await messageBus.DeclareQueueAsync(queueName, ct);
        _subscription = await messageBus.SubscribeAsync<ResourceRequest>(queueName, HandleAsync, ct);

        logger.LogInformation("ResourceProxyHandler started — listening on {QueueName}.", queueName);
    }

    private async Task HandleAsync(ResourceRequest request, CancellationToken ct)
    {
        var responseTopic = WorkflowQueues.ResourceResponseQueueFor(request.WorkflowInstanceId);

        if (dispatcherSettings.Value.RequireInstanceToken &&
            !tokenRegistry.Validate(request.WorkflowInstanceId, request.InstanceToken))
        {
            logger.LogWarning(
                "Rejected ResourceRequest with missing or invalid instance token. InstanceId={InstanceId} Resource={ResourceName}",
                request.WorkflowInstanceId, request.ResourceName);
            await auditLog.AppendAsync(
                "steering-instance", "workflow.resource.rejected",
                request.WorkflowInstanceId.ToString(), "invalid-instance-token", ct: ct);
            return;
        }

        var connector = connectors.FirstOrDefault(c => c.ResourceName == request.ResourceName);
        if (connector is null)
        {
            logger.LogWarning(
                "ResourceRequest for unknown resource {ResourceName}. InstanceId={InstanceId}",
                request.ResourceName, request.WorkflowInstanceId);
            await messageBus.PublishAsync(responseTopic, new ResourceResponse(
                request.RequestId, false, $"Unknown resource '{request.ResourceName}'", null), ct);
            return;
        }

        await auditLog.AppendAsync(
            "steering-instance", "workflow.resource-call",
            request.WorkflowInstanceId.ToString(),
            $"{request.ResourceName}:{request.Operation}", ct: ct);

        try
        {
            var result = await connector.ExecuteAsync(request.Operation, request.PayloadJson, ct);
            await messageBus.PublishAsync(responseTopic, new ResourceResponse(
                request.RequestId, true, null, result), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Resource call failed. InstanceId={InstanceId} Resource={ResourceName} Operation={Operation}",
                request.WorkflowInstanceId, request.ResourceName, request.Operation);
            await messageBus.PublishAsync(responseTopic, new ResourceResponse(
                request.RequestId, false, ex.Message, null), ct);
        }
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}

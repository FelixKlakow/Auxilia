using System.Collections.Concurrent;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class WorkflowRegistrationHandler(
    IMessageBusClient messageBus,
    EnvironmentValidator environmentValidator,
    ConfigurationResolver configResolver,
    WorkflowInstanceRegistry instanceRegistry,
    ILogger<WorkflowRegistrationHandler> logger)
{
    private readonly ConcurrentDictionary<Guid, byte> _registeredInstances = new();
    private IAsyncDisposable? _subscription;

    public int RegisteredCount => _registeredInstances.Count;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await messageBus.DeclareQueueAsync("workflow-registration", cancellationToken);
        _subscription = await messageBus.SubscribeAsync<WorkflowRegistrationRequest>(
            "workflow-registration", HandleAsync, cancellationToken);
    }

    private async Task HandleAsync(WorkflowRegistrationRequest request, CancellationToken cancellationToken)
    {
        var envResult = environmentValidator.Validate(request.Manifest);
        if (!envResult.IsValid)
        {
            logger.LogInformation(
                "Workflow registration rejected for instance {WorkflowInstanceId}: environment requirements not satisfied — {Reasons}.",
                request.WorkflowInstanceId,
                string.Join("; ", envResult.UnsatisfiedRequirements));

            await messageBus.PublishAsync(request.ResponseTopic, new WorkflowConfigurationResponse(
                request.WorkflowInstanceId,
                false,
                string.Join("; ", envResult.UnsatisfiedRequirements),
                new Dictionary<string, EncryptedSlotConfiguration>()),
                cancellationToken);
            return;
        }

        var resolverResult = configResolver.Resolve(request.Manifest.WorkflowName, request.PublicKey);
        if (!resolverResult.IsSuccess)
        {
            logger.LogInformation(
                "Workflow registration rejected for instance {WorkflowInstanceId}: configuration resolution failed — {Reason}.",
                request.WorkflowInstanceId,
                resolverResult.FailureReason);

            await messageBus.PublishAsync(request.ResponseTopic, new WorkflowConfigurationResponse(
                request.WorkflowInstanceId,
                false,
                resolverResult.FailureReason,
                new Dictionary<string, EncryptedSlotConfiguration>()),
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "Workflow registration succeeded for instance {WorkflowInstanceId}.",
            request.WorkflowInstanceId);

        await messageBus.PublishAsync(request.ResponseTopic, new WorkflowConfigurationResponse(
            request.WorkflowInstanceId,
            true,
            null,
            resolverResult.Slots,
            resolverResult.SignalHandlers),
            cancellationToken);

        _registeredInstances.TryAdd(request.WorkflowInstanceId, 0);
        instanceRegistry.Register(request.WorkflowInstanceId, request.Manifest.WorkflowName);
    }
}

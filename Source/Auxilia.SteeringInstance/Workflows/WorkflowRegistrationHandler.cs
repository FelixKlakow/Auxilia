using System.Collections.Concurrent;
using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class WorkflowRegistrationHandler(
    IMessageBusClient messageBus,
    EnvironmentValidator environmentValidator,
    ConfigurationResolver configResolver,
    WorkflowInstanceRegistry instanceRegistry,
    WorkflowInstanceTokenRegistry tokenRegistry,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
    ILogger<WorkflowRegistrationHandler> logger)
{
    private readonly ConcurrentDictionary<Guid, byte> _registeredInstances = new();
    private IAsyncDisposable? _subscription;

    public int RegisteredCount => _registeredInstances.Count;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var queueName = dispatcherSettings.Value.RegistrationQueueName;
        await messageBus.DeclareQueueAsync(queueName, cancellationToken);
        _subscription = await messageBus.SubscribeAsync<WorkflowRegistrationRequest>(
            queueName, HandleAsync, cancellationToken);
    }

    private async Task HandleAsync(WorkflowRegistrationRequest request, CancellationToken cancellationToken)
    {
        var responseTopic = request.ResponseTopic;
        if (dispatcherSettings.Value.RequireInstanceToken)
        {
            if (!tokenRegistry.Validate(request.WorkflowInstanceId, request.InstanceToken))
            {
                logger.LogWarning(
                    "Rejected WorkflowRegistrationRequest with missing or invalid instance token. Workflow={WorkflowName} InstanceId={InstanceId}",
                    request.Manifest.WorkflowName, request.WorkflowInstanceId);
                return;
            }

            // One registration per launch: the token is consumed regardless of the outcome,
            // and the response goes only to the queue the platform pre-created at launch.
            tokenRegistry.Consume(request.WorkflowInstanceId);
            responseTopic = WorkflowQueues.ResponseQueueFor(request.WorkflowInstanceId);
        }

        var envResult = environmentValidator.Validate(request.Manifest);
        if (!envResult.IsValid)
        {
            logger.LogInformation(
                "Workflow registration rejected for instance {WorkflowInstanceId}: environment requirements not satisfied — {Reasons}.",
                request.WorkflowInstanceId,
                string.Join("; ", envResult.UnsatisfiedRequirements));

            await messageBus.PublishAsync(responseTopic, new WorkflowConfigurationResponse(
                request.WorkflowInstanceId,
                false,
                string.Join("; ", envResult.UnsatisfiedRequirements),
                new Dictionary<string, EncryptedSlotConfiguration>()),
                cancellationToken);
            return;
        }

        // Workflows that declare no slots need no configuration resolution.
        if (request.Manifest.Slots.Count == 0)
        {
            logger.LogInformation(
                "Workflow {WorkflowInstanceId} declares no slots — responding with empty configuration.",
                request.WorkflowInstanceId);
            var signalHandlers = configResolver.ResolveSignalHandlers(request.Manifest.WorkflowName);
            await messageBus.PublishAsync(responseTopic, new WorkflowConfigurationResponse(
                request.WorkflowInstanceId, true, null,
                new Dictionary<string, EncryptedSlotConfiguration>(),
                signalHandlers),
                cancellationToken);
            _registeredInstances.TryAdd(request.WorkflowInstanceId, 0);
            return;
        }

        var resolverResult = configResolver.Resolve(request.Manifest.WorkflowName, request.PublicKey);
        if (!resolverResult.IsSuccess)
        {
            logger.LogInformation(
                "Workflow registration rejected for instance {WorkflowInstanceId}: configuration resolution failed — {Reason}.",
                request.WorkflowInstanceId,
                resolverResult.FailureReason);

            await messageBus.PublishAsync(responseTopic, new WorkflowConfigurationResponse(
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

        await messageBus.PublishAsync(responseTopic, new WorkflowConfigurationResponse(
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

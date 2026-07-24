using System.Collections.Concurrent;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

public sealed class WorkflowRegistrationHandler(
    IMessageBusClient messageBus,
    EnvironmentValidator environmentValidator,
    ConfigurationResolver configResolver,
    WorkflowInstanceRegistry instanceRegistry,
    WorkflowInstanceTokenRegistry tokenRegistry,
    DirtyConfigurationDetector dirtyDetector,
    AuditLog auditLog,
    WorkflowStatusPublisher statusPublisher,
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
                await auditLog.AppendAsync(
                    "steering-instance", "workflow.registration.rejected",
                    request.WorkflowInstanceId.ToString(), "invalid-instance-token",
                    ct: cancellationToken);
                return;
            }

            // One registration per launch; the token then stays valid as the instance
            // credential for just-in-time slot activations until the run terminates.
            // The response goes only to the queue the platform pre-created at launch.
            if (!tokenRegistry.TryBeginRegistration(request.WorkflowInstanceId, request.InstanceToken))
            {
                logger.LogWarning(
                    "Rejected duplicate WorkflowRegistrationRequest. Workflow={WorkflowName} InstanceId={InstanceId}",
                    request.Manifest.WorkflowName, request.WorkflowInstanceId);
                await auditLog.AppendAsync(
                    "steering-instance", "workflow.registration.rejected",
                    request.WorkflowInstanceId.ToString(), "duplicate-registration", ct: cancellationToken);
                return;
            }
            responseTopic = WorkflowQueues.ResponseQueueFor(request.WorkflowInstanceId);
        }

        // One-shot is the security default: long-living deployment needs operator approval.
        if (request.Manifest.Lifetime == Auxilia.Workflows.WorkflowLifetime.LongLiving &&
            !dispatcherSettings.Value.ApprovedLongLivingWorkflowTypes.Contains(request.Manifest.WorkflowName))
        {
            logger.LogWarning(
                "Rejected long-living registration for {WorkflowName}: type is not operator-approved.",
                request.Manifest.WorkflowName);
            await auditLog.AppendAsync(
                "steering-instance", "workflow.registration.rejected",
                request.WorkflowInstanceId.ToString(), "long-living-not-approved", ct: cancellationToken);
            await messageBus.PublishAsync(responseTopic, new WorkflowConfigurationResponse(
                request.WorkflowInstanceId,
                false,
                "Long-living lifetime requires operator approval (WorkflowDispatcher:ApprovedLongLivingWorkflowTypes).",
                new Dictionary<string, EncryptedSlotConfiguration>()),
                cancellationToken);
            return;
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

        // The manifest is the authoritative schema of this package version: persist it so the
        // registry knows the slots of docker-baked workflows too, and mark slot configurations
        // dirty when a new version adds required capability fields.
        await dirtyDetector.DetectAsync(request.Manifest.WorkflowName, SchemaOf(request.Manifest), cancellationToken);

        // Workflows that declare no slots need no configuration resolution.
        if (request.Manifest.Slots.Count == 0)
        {
            logger.LogInformation(
                "Workflow {WorkflowInstanceId} declares no slots — responding with empty configuration.",
                request.WorkflowInstanceId);
            var signalHandlers = await configResolver.ResolveSignalHandlersAsync(
                request.Manifest.WorkflowName, cancellationToken);
            await messageBus.PublishAsync(responseTopic, new WorkflowConfigurationResponse(
                request.WorkflowInstanceId, true, null,
                new Dictionary<string, EncryptedSlotConfiguration>(),
                signalHandlers),
                cancellationToken);
            _registeredInstances.TryAdd(request.WorkflowInstanceId, 0);
            await instanceRegistry.RegisterAsync(
                request.WorkflowInstanceId, request.Manifest.WorkflowName,
                request.Manifest.Lifetime.ToString(),
                System.Text.Json.JsonSerializer.Serialize(request.Manifest.Outputs),
                System.Text.Json.JsonSerializer.Serialize(request.Manifest.Views), cancellationToken);
            await statusPublisher.PublishAsync(
                request.WorkflowInstanceId, request.Manifest.WorkflowName, "Running", ct: cancellationToken);
            await auditLog.AppendAsync(
                "steering-instance", "workflow.registration.accepted",
                request.WorkflowInstanceId.ToString(), "success", ct: cancellationToken);
            return;
        }

        // Pre-flight only: verify the slot configuration source is valid. No credentials are
        // delivered at registration — slots activate just-in-time (ARCHITECTURE §4/§7).
        // Instances dispatched from a named configuration (#18) validate that configuration's
        // bindings; all others keep the global (workflow type, slot) table.
        var instanceRecord = await instanceRegistry.GetAsync(request.WorkflowInstanceId, cancellationToken);
        var (configsValid, invalidReason) = instanceRecord?.WorkflowConfigurationId is { } configurationId
            ? await configResolver.ValidateConfigurationAsync(configurationId, cancellationToken)
            : await configResolver.ValidateConfiguredAsync(
                request.Manifest.WorkflowName, cancellationToken);
        if (!configsValid)
        {
            logger.LogInformation(
                "Workflow registration rejected for instance {WorkflowInstanceId}: configuration validation failed — {Reason}.",
                request.WorkflowInstanceId,
                invalidReason);

            await messageBus.PublishAsync(responseTopic, new WorkflowConfigurationResponse(
                request.WorkflowInstanceId,
                false,
                invalidReason,
                new Dictionary<string, EncryptedSlotConfiguration>()),
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "Workflow registration succeeded for instance {WorkflowInstanceId}.",
            request.WorkflowInstanceId);

        var slottedSignalHandlers = await configResolver.ResolveSignalHandlersAsync(
            request.Manifest.WorkflowName, cancellationToken);
        await messageBus.PublishAsync(responseTopic, new WorkflowConfigurationResponse(
            request.WorkflowInstanceId,
            true,
            null,
            new Dictionary<string, EncryptedSlotConfiguration>(),
            slottedSignalHandlers),
            cancellationToken);

        _registeredInstances.TryAdd(request.WorkflowInstanceId, 0);
        await instanceRegistry.RegisterAsync(
            request.WorkflowInstanceId, request.Manifest.WorkflowName,
            request.Manifest.Lifetime.ToString(),
            System.Text.Json.JsonSerializer.Serialize(request.Manifest.Outputs),
                System.Text.Json.JsonSerializer.Serialize(request.Manifest.Views), cancellationToken);
        await statusPublisher.PublishAsync(
            request.WorkflowInstanceId, request.Manifest.WorkflowName, "Running", ct: cancellationToken);
        await auditLog.AppendAsync(
            "steering-instance", "workflow.registration.accepted",
            request.WorkflowInstanceId.ToString(), "success", ct: cancellationToken);
    }

    private static Auxilia.Workflows.WorkflowSchema SchemaOf(Auxilia.Workflows.WorkflowManifest manifest)
        => new(manifest.WorkflowName, manifest.Slots, manifest.EnvironmentRequirements)
        {
            Version = manifest.Version,
            Tags = manifest.Tags,
            Outputs = manifest.Outputs,
            Signals = manifest.Signals,
            Lifetime = manifest.Lifetime,
            Views = manifest.Views,
            NetworkEndpoints = manifest.NetworkEndpoints,
            Repositories = manifest.Repositories,
            Triggers = manifest.Triggers,
            // Every field must ride along: a run-time registration REPLACES the stored
            // schema, so anything omitted here is wiped by the workflow's first run.
            Inputs = manifest.Inputs,
            ConsumedArtifacts = manifest.ConsumedArtifacts,
            InteractiveTerminalPort = manifest.InteractiveTerminalPort
        };
}

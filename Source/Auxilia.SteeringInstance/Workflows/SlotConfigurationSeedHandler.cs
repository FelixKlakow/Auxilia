using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class SlotConfigurationSeedHandler(
    IMessageBusClient messageBus,
    SlotConfigurationStore slotStore,
    SlotProviderRegistry providerRegistry,
    WorkflowConfigurationStore configurationStore,
    LongLivingDrainCoordinator drainCoordinator,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
    ILogger<SlotConfigurationSeedHandler> logger)
{
    public async Task StartAsync(CancellationToken ct)
    {
        await messageBus.DeclareExchangeAsync("slot-configurations", ct);
        await messageBus.SubscribeToExchangeAsync<UpsertSlotConfigurationCommand>("slot-configurations", HandleUpsertAsync, ct);
        await messageBus.SubscribeToExchangeAsync<RemoveSlotConfigurationCommand>("slot-configurations", HandleRemoveSlotAsync, ct);
        await messageBus.SubscribeToExchangeAsync<RegisterSlotProviderCommand>("slot-configurations", HandleRegisterProviderAsync, ct);
        await messageBus.SubscribeToExchangeAsync<RemoveSlotProviderCommand>("slot-configurations", HandleRemoveProviderAsync, ct);
        await messageBus.SubscribeToExchangeAsync<UpsertWorkflowConfigurationCommand>("slot-configurations", HandleUpsertWorkflowConfigurationAsync, ct);

        // Per-instance seed queues: one sub-queue per command type so that each typed consumer
        // only receives messages it can deserialize. Using a single queue with multiple competing
        // typed consumers causes round-robin delivery, which silently drops messages when the wrong
        // consumer receives a message it cannot deserialize.
        var seedBase = dispatcherSettings.Value.CommandQueueName + "-slot-seed";
        var upsertQueue              = seedBase + ".upsert";
        var removeQueue              = seedBase + ".remove";
        var registerQueue            = seedBase + ".register";
        var removeProviderQueue      = seedBase + ".remove-provider";
        var upsertConfigurationQueue = seedBase + ".upsert-configuration";

        await messageBus.DeclareQueueAsync(upsertQueue,              ct);
        await messageBus.DeclareQueueAsync(removeQueue,              ct);
        await messageBus.DeclareQueueAsync(registerQueue,            ct);
        await messageBus.DeclareQueueAsync(removeProviderQueue,      ct);
        await messageBus.DeclareQueueAsync(upsertConfigurationQueue, ct);

        await messageBus.SubscribeAsync<UpsertSlotConfigurationCommand>(upsertQueue,         HandleUpsertAsync,          ct);
        await messageBus.SubscribeAsync<RemoveSlotConfigurationCommand>(removeQueue,         HandleRemoveSlotAsync,      ct);
        await messageBus.SubscribeAsync<RegisterSlotProviderCommand>   (registerQueue,       HandleRegisterProviderAsync, ct);
        await messageBus.SubscribeAsync<RemoveSlotProviderCommand>     (removeProviderQueue, HandleRemoveProviderAsync,  ct);
        await messageBus.SubscribeAsync<UpsertWorkflowConfigurationCommand>(upsertConfigurationQueue, HandleUpsertWorkflowConfigurationAsync, ct);

        logger.LogInformation("SlotConfigurationSeedHandler started — subscribed to slot-configurations exchange.");
    }

    private async Task HandleUpsertAsync(UpsertSlotConfigurationCommand cmd, CancellationToken ct)
    {
        await slotStore.UpsertConfigurationAsync(cmd.WorkflowType,
            new StoredSlotConfiguration(cmd.SlotName, cmd.ProviderType, cmd.Settings, ConfigurationStatus.Valid), ct);
        logger.LogInformation(
            "Upserted slot configuration. WorkflowType={WorkflowType} SlotName={SlotName} ProviderType={ProviderType}",
            cmd.WorkflowType, cmd.SlotName, cmd.ProviderType);
        await drainCoordinator.DrainRunningInstancesAsync(cmd.WorkflowType, ct);
    }

    private async Task HandleRemoveSlotAsync(RemoveSlotConfigurationCommand cmd, CancellationToken ct)
    {
        await slotStore.RemoveConfigurationAsync(cmd.WorkflowType, cmd.SlotName, ct);
        logger.LogInformation(
            "Removed slot configuration. WorkflowType={WorkflowType} SlotName={SlotName}",
            cmd.WorkflowType, cmd.SlotName);
        await drainCoordinator.DrainRunningInstancesAsync(cmd.WorkflowType, ct);
    }

    private async Task HandleRegisterProviderAsync(RegisterSlotProviderCommand cmd, CancellationToken ct)
    {
        await providerRegistry.UpsertAsync(cmd.ProviderType, cmd.DllPath, ct);
        logger.LogInformation(
            "Registered slot provider. ProviderType={ProviderType} DllPath={DllPath}",
            cmd.ProviderType, cmd.DllPath);
    }

    private async Task HandleRemoveProviderAsync(RemoveSlotProviderCommand cmd, CancellationToken ct)
    {
        await providerRegistry.RemoveAsync(cmd.ProviderType, ct);
        logger.LogInformation("Removed slot provider. ProviderType={ProviderType}", cmd.ProviderType);
    }

    private async Task HandleUpsertWorkflowConfigurationAsync(
        UpsertWorkflowConfigurationCommand cmd, CancellationToken ct)
    {
        try
        {
            await configurationStore.UpsertAsync(new StoredWorkflowConfiguration(
                cmd.Name, cmd.DisplayName, cmd.WorkflowType, cmd.PackageUri, cmd.Enabled,
                cmd.SlotBindings
                    .Select(b => new StoredSlotBinding(b.SlotName, b.ProviderType, b.Settings))
                    .ToList(),
                cmd.OwnerPrincipalId), ct);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex,
                "Rejected workflow configuration upsert. Name={Name}", cmd.Name);
            return;
        }

        logger.LogInformation(
            "Upserted workflow configuration. Name={Name} WorkflowType={WorkflowType} Bindings={BindingCount}",
            cmd.Name, cmd.WorkflowType, cmd.SlotBindings.Count);
        await drainCoordinator.DrainRunningInstancesAsync(cmd.WorkflowType, ct);
    }
}

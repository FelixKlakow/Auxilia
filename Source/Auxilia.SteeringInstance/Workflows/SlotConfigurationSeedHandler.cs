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

        // Per-instance seed queue: allows targeted config delivery without fanout broadcast.
        // Queue name is deterministic from CommandQueueName — no extra env var needed.
        var seedQueue = dispatcherSettings.Value.CommandQueueName + "-slot-seed";
        await messageBus.DeclareQueueAsync(seedQueue, ct);
        await messageBus.SubscribeAsync<UpsertSlotConfigurationCommand>(seedQueue, HandleUpsertAsync, ct);
        await messageBus.SubscribeAsync<RemoveSlotConfigurationCommand>(seedQueue, HandleRemoveSlotAsync, ct);
        await messageBus.SubscribeAsync<RegisterSlotProviderCommand>(seedQueue, HandleRegisterProviderAsync, ct);
        await messageBus.SubscribeAsync<RemoveSlotProviderCommand>(seedQueue, HandleRemoveProviderAsync, ct);

        logger.LogInformation("SlotConfigurationSeedHandler started — subscribed to slot-configurations exchange.");
    }

    private Task HandleUpsertAsync(UpsertSlotConfigurationCommand cmd, CancellationToken ct)
    {
        slotStore.UpsertConfiguration(cmd.WorkflowType,
            new StoredSlotConfiguration(cmd.SlotName, cmd.ProviderType, cmd.Settings, ConfigurationStatus.Valid));
        logger.LogInformation(
            "Upserted slot configuration. WorkflowType={WorkflowType} SlotName={SlotName} ProviderType={ProviderType}",
            cmd.WorkflowType, cmd.SlotName, cmd.ProviderType);
        return Task.CompletedTask;
    }

    private Task HandleRemoveSlotAsync(RemoveSlotConfigurationCommand cmd, CancellationToken ct)
    {
        slotStore.RemoveConfiguration(cmd.WorkflowType, cmd.SlotName);
        logger.LogInformation(
            "Removed slot configuration. WorkflowType={WorkflowType} SlotName={SlotName}",
            cmd.WorkflowType, cmd.SlotName);
        return Task.CompletedTask;
    }

    private Task HandleRegisterProviderAsync(RegisterSlotProviderCommand cmd, CancellationToken ct)
    {
        providerRegistry.Upsert(cmd.ProviderType, cmd.DllPath);
        logger.LogInformation(
            "Registered slot provider. ProviderType={ProviderType} DllPath={DllPath}",
            cmd.ProviderType, cmd.DllPath);
        return Task.CompletedTask;
    }

    private Task HandleRemoveProviderAsync(RemoveSlotProviderCommand cmd, CancellationToken ct)
    {
        providerRegistry.Remove(cmd.ProviderType);
        logger.LogInformation("Removed slot provider. ProviderType={ProviderType}", cmd.ProviderType);
        return Task.CompletedTask;
    }
}

using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.Workflows;

public sealed class WorkflowBootstrapper(
    WorkflowConfigurationResponse response,
    EphemeralKeyPair keyPair,
    ISlotHandlerResolver resolver,
    IReadOnlyList<SlotDefinition> slotDefinitions,
    Guid instanceId = default)
{
    public void Apply(IServiceCollection services)
    {
        foreach (var (slotName, encryptedSlot) in response.Slots)
        {
            var definition = slotDefinitions.FirstOrDefault(d => d.SlotName == slotName);
            var serviceType = definition?.ServiceType ?? typeof(object);

            var config = SlotConfigurationCrypto.Decrypt(encryptedSlot, keyPair);
            var handler = resolver.Resolve(config.ProviderType);
            handler.Register(services, slotName, serviceType, config);
        }

        var contextId = instanceId == default ? Guid.NewGuid() : instanceId;
        services.AddSingleton(new WorkflowInstanceContext(contextId));
        services.TryAddSingleton<ISignalEmitter, DefaultSignalEmitter>();
    }
}

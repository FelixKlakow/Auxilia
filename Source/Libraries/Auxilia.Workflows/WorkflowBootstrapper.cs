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
    Guid instanceId = default,
    IReadOnlyDictionary<string, SlotConfiguration>? activatedConfigurations = null)
{
    public void Apply(IServiceCollection services)
    {
        if (response.Slots.Count > 0)
        {
            // Eager delivery: all slot configurations arrived with the registration response
            // (test harness / dev mode without a Core.Runner activation handler).
            foreach (var (slotName, encryptedSlot) in response.Slots)
            {
                var definition = slotDefinitions.FirstOrDefault(d => d.SlotName == slotName);
                var serviceType = definition?.ServiceType ?? typeof(object);

                var config = SlotConfigurationCrypto.Decrypt(encryptedSlot, keyPair);
                var handler = resolver.Resolve(config.ProviderType);
                handler.Register(services, slotName, serviceType, config);
            }
        }
        else if (activatedConfigurations is not null)
        {
            // Just-in-time delivery: each configuration was fetched via an individual,
            // audited SlotActivationRequest — never as a bundle at registration.
            foreach (var definition in slotDefinitions)
            {
                if (!activatedConfigurations.TryGetValue(definition.SlotName, out var config))
                {
                    // Optional slots may stay unbound — the capability simply isn't registered.
                    if (definition.Optional)
                        continue;
                    throw new InvalidOperationException(
                        $"No activated configuration for slot '{definition.SlotName}'.");
                }

                var handler = resolver.Resolve(config.ProviderType);
                handler.Register(services, definition.SlotName, definition.ServiceType, config);
            }
        }

        var contextId = instanceId == default ? Guid.NewGuid() : instanceId;
        services.AddSingleton(new WorkflowInstanceContext(contextId));
        services.TryAddSingleton<ISignalEmitter, DefaultSignalEmitter>();
    }
}

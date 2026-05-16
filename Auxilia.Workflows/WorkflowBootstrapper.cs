using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows;

public sealed class WorkflowBootstrapper(WorkflowConfigurationResponse response, EphemeralKeyPair keyPair)
{
    public void Apply(IServiceCollection services)
    {
        foreach (var (_, encryptedSlot) in response.Slots)
        {
            var config = SlotConfigurationCrypto.Decrypt(encryptedSlot, keyPair);
            var handler = SlotHandlerRegistry.Resolve(config.ProviderType);
            handler.Register(services, config);
        }
    }
}

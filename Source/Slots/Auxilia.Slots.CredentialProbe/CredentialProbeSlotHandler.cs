using Auxilia.Workflows;
using Auxilia.Workflows.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.CredentialProbe;

/// <summary>
/// Test-fake slot handler backing <see cref="ICredentialProbe"/>: exposes the decrypted slot
/// settings (resolved just-in-time through the Core) so a workflow can prove the credential
/// arrived. Registered under the "credential-probe" provider type.
/// </summary>
public sealed class CredentialProbeSlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
        => services.AddKeyedScoped<ICredentialProbe>(
            slotName, (_, _) => new SettingsCredentialProbe(configuration.Settings));

    private sealed class SettingsCredentialProbe(IReadOnlyDictionary<string, string> settings) : ICredentialProbe
    {
        public string? GetSetting(string key) => settings.GetValueOrDefault(key);
    }
}

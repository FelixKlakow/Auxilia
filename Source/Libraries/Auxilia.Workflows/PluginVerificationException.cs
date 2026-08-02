namespace Auxilia.Workflows;

public sealed class PluginVerificationException : Exception
{
    public string ProviderType { get; }

    public PluginVerificationException(string providerType)
        : base($"Plugin manifest verification failed for provider '{providerType}'.")
    {
        ProviderType = providerType;
    }
}

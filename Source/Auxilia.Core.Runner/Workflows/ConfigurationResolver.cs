using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;

namespace Auxilia.Core.Runner.Workflows;

public sealed class ConfigurationResolver(
    SlotConfigurationStore store,
    SignalHandlerStore signalHandlerStore,
    WorkflowConfigurationStore configurationStore,
    ILogger<ConfigurationResolver> logger)
{
    /// <summary>
    /// Registration-time pre-flight: all slot configurations for the type must exist and be
    /// valid. No secrets are resolved here — credentials are delivered just-in-time per slot.
    /// </summary>
    public async Task<(bool IsValid, string? Reason)> ValidateConfiguredAsync(
        string workflowTypeName, CancellationToken ct = default)
    {
        var configurations = await store.GetConfigurationsAsync(workflowTypeName, ct);

        if (configurations.Count == 0)
        {
            logger.LogWarning(
                "No slot configurations found for workflow type '{WorkflowType}'.",
                workflowTypeName);
            return (false, "No slot configurations found for workflow type");
        }

        if (configurations.Any(c => c.Status == ConfigurationStatus.Dirty))
        {
            logger.LogWarning(
                "One or more slot configurations for '{WorkflowType}' are dirty.",
                workflowTypeName);
            return (false, "One or more slot configurations are dirty and must be reconfigured");
        }

        return (true, null);
    }

    /// <summary>Resolves and encrypts a single slot's configuration at activation time.</summary>
    public async Task<(bool Success, string? Error, EncryptedSlotConfiguration? Slot)> ResolveSlotAsync(
        string workflowTypeName, string slotName, string publicKeyBase64, CancellationToken ct = default)
    {
        var configurations = await store.GetConfigurationsAsync(workflowTypeName, ct);
        var config = configurations.FirstOrDefault(c => c.SlotName == slotName);

        if (config is null)
        {
            logger.LogWarning(
                "No configuration for slot '{SlotName}' of workflow type '{WorkflowType}'.",
                slotName, workflowTypeName);
            return (false, $"No configuration for slot '{slotName}'", null);
        }

        if (config.Status == ConfigurationStatus.Dirty)
            return (false, $"Configuration for slot '{slotName}' is dirty and must be reconfigured", null);

        return EncryptSlot(config.ProviderType, config.Settings, publicKeyBase64);
    }

    /// <summary>
    /// Registration-time pre-flight for instances dispatched from a named workflow
    /// configuration (#18): the configuration must exist, be enabled, and bind at least one slot.
    /// </summary>
    public async Task<(bool IsValid, string? Reason)> ValidateConfigurationAsync(
        Guid configurationId, CancellationToken ct = default)
    {
        var configuration = await configurationStore.GetAsync(configurationId, ct);
        if (configuration is null)
            return (false, $"Workflow configuration '{configurationId}' not found");
        if (!configuration.Enabled)
            return (false, $"Workflow configuration '{configuration.Name}' is disabled");
        if (configuration.SlotBindings.Count == 0)
            return (false, $"Workflow configuration '{configuration.Name}' has no slot bindings");
        if (configuration.SlotBindings.FirstOrDefault(b => !b.SlotInstanceResolved) is { } unresolved)
            return (false,
                $"Slot binding '{unresolved.SlotName}' of workflow configuration '{configuration.Name}' references a deleted slot instance");
        return (true, null);
    }

    /// <summary>
    /// Resolves and encrypts a single slot's settings from a named workflow configuration's
    /// bindings at activation time (#18) — used instead of the global (type, slot) table when
    /// the instance was dispatched from a configuration.
    /// </summary>
    public async Task<(bool Success, string? Error, EncryptedSlotConfiguration? Slot)> ResolveConfigurationSlotAsync(
        Guid configurationId, string slotName, string publicKeyBase64, CancellationToken ct = default)
    {
        var configuration = await configurationStore.GetAsync(configurationId, ct);
        if (configuration is null)
        {
            logger.LogWarning("Workflow configuration '{ConfigurationId}' not found.", configurationId);
            return (false, $"Workflow configuration '{configurationId}' not found", null);
        }

        if (!configuration.Enabled)
            return (false, $"Workflow configuration '{configuration.Name}' is disabled", null);

        var binding = configuration.SlotBindings.FirstOrDefault(b => b.SlotName == slotName);
        if (binding is null)
        {
            logger.LogWarning(
                "No binding for slot '{SlotName}' in workflow configuration '{ConfigurationName}'.",
                slotName, configuration.Name);
            return (false,
                $"No binding for slot '{slotName}' in workflow configuration '{configuration.Name}'", null);
        }

        if (!binding.SlotInstanceResolved)
        {
            logger.LogWarning(
                "Slot '{SlotName}' of configuration '{ConfigurationName}' references a deleted slot instance.",
                slotName, configuration.Name);
            return (false,
                $"Slot '{slotName}' of configuration '{configuration.Name}' references a deleted slot instance", null);
        }

        return EncryptSlot(binding.ProviderType, binding.Settings, publicKeyBase64);
    }

    private (bool Success, string? Error, EncryptedSlotConfiguration? Slot) EncryptSlot(
        string providerType, IReadOnlyDictionary<string, string> settings, string publicKeyBase64)
    {
        byte[] publicKeyDer;
        try
        {
            publicKeyDer = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException ex)
        {
            logger.LogError(ex, "Public key is not valid base-64.");
            return (false, "Public key is not valid base-64", null);
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(publicKeyDer, out _);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Failed to import RSA public key.");
            return (false, "Failed to import RSA public key", null);
        }

        var settingsJson = JsonSerializer.Serialize(settings);
        var cipherBytes = rsa.Encrypt(
            System.Text.Encoding.UTF8.GetBytes(settingsJson),
            RSAEncryptionPadding.OaepSHA256);
        var encrypted = new EncryptedSlotConfiguration(
            providerType,
            Convert.ToBase64String(cipherBytes));

        return (true, null, encrypted);
    }

    /// <summary>
    /// Returns the signal handlers registered for <paramref name="workflowTypeName"/>
    /// without performing slot resolution. Used when a workflow declares no slots.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ISignalHandlerDescriptor>> ResolveSignalHandlersAsync(
        string workflowTypeName, CancellationToken ct = default)
    {
        var signalHandlers = await signalHandlerStore.GetHandlersAsync(workflowTypeName, ct);
        var handlerMap = new Dictionary<string, ISignalHandlerDescriptor>(signalHandlers.Count);
        foreach (var handler in signalHandlers)
            handlerMap[handler.SignalName] = handler.HandlerDescriptor;
        return handlerMap;
    }
}

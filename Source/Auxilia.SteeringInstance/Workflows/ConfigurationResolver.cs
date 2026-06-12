using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class ConfigurationResolver(
    SlotConfigurationStore store,
    SignalHandlerStore signalHandlerStore,
    ILogger<ConfigurationResolver> logger)
{
    public async Task<ResolverResult> ResolveAsync(
        string workflowTypeName, string publicKeyBase64, CancellationToken ct = default)
    {
        var configurations = await store.GetConfigurationsAsync(workflowTypeName, ct);

        if (configurations.Count == 0)
        {
            logger.LogWarning(
                "No slot configurations found for workflow type '{WorkflowType}'.",
                workflowTypeName);
            return ResolverResult.Fail("No slot configurations found for workflow type");
        }

        if (configurations.Any(c => c.Status == ConfigurationStatus.Dirty))
        {
            logger.LogWarning(
                "One or more slot configurations for '{WorkflowType}' are dirty.",
                workflowTypeName);
            return ResolverResult.Fail(
                "One or more slot configurations are dirty and must be reconfigured");
        }

        byte[] publicKeyDer;
        try
        {
            publicKeyDer = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException ex)
        {
            logger.LogError(ex, "Public key is not valid base-64.");
            return ResolverResult.Fail("Public key is not valid base-64");
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(publicKeyDer, out _);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Failed to import RSA public key.");
            return ResolverResult.Fail("Failed to import RSA public key");
        }

        var encrypted = new Dictionary<string, EncryptedSlotConfiguration>(configurations.Count);
        foreach (var config in configurations)
        {
            var settingsJson = JsonSerializer.Serialize(config.Settings);
            var cipherBytes = rsa.Encrypt(
                System.Text.Encoding.UTF8.GetBytes(settingsJson),
                RSAEncryptionPadding.OaepSHA256);
            encrypted[config.SlotName] = new EncryptedSlotConfiguration(
                config.ProviderType,
                Convert.ToBase64String(cipherBytes));
        }

        var handlerMap = await ResolveSignalHandlersAsync(workflowTypeName, ct);

        return ResolverResult.Ok(encrypted, handlerMap);
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

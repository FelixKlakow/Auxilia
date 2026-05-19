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
    public ResolverResult Resolve(string workflowTypeName, string publicKeyBase64)
    {
        var configurations = store.GetConfigurations(workflowTypeName);

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

        var signalHandlers = signalHandlerStore.GetHandlers(workflowTypeName);
        var handlerMap = new Dictionary<string, ISignalHandlerDescriptor>(signalHandlers.Count);
        foreach (var handler in signalHandlers)
            handlerMap[handler.SignalName] = handler.HandlerDescriptor;

        return ResolverResult.Ok(encrypted, handlerMap);
    }
}

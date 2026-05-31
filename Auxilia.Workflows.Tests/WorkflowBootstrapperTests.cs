using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class WorkflowBootstrapperTests
{
    private static EncryptedSlotConfiguration EncryptSlot(string providerType, Dictionary<string, string> settings, string publicKeyBase64)
    {
        var json = JsonSerializer.Serialize(settings);

        var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

        var ciphertext = Convert.ToBase64String(
            rsa.Encrypt(Encoding.UTF8.GetBytes(json), RSAEncryptionPadding.OaepSHA256));

        return new EncryptedSlotConfiguration(providerType, ciphertext);
    }

    [Test]
    public void Apply_CallsRegisterOncePerSlot_WithCorrectConfiguration()
    {
        var providerType = $"bt-provider-{Guid.NewGuid()}";
        using var keyPair = new EphemeralKeyPair();

        var slot1 = EncryptSlot(providerType, new Dictionary<string, string> { ["a"] = "1" }, keyPair.PublicKeyBase64);
        var slot2 = EncryptSlot(providerType, new Dictionary<string, string> { ["b"] = "2" }, keyPair.PublicKeyBase64);

        var slots = new Dictionary<string, EncryptedSlotConfiguration>
        {
            ["slot1"] = slot1,
            ["slot2"] = slot2
        };
        var response = new WorkflowConfigurationResponse(Guid.NewGuid(), true, null, slots);

        var spy = new SpySlotHandler();
        var resolver = new SlotHandlerResolver();
        resolver.Register(providerType, spy);

        var slotDefinitions = new List<SlotDefinition>
        {
            new("slot1", null) { ServiceType = typeof(object) },
            new("slot2", null) { ServiceType = typeof(object) }
        };

        var services = new ServiceCollection();
        var bootstrapper = new WorkflowBootstrapper(response, keyPair, resolver, slotDefinitions);
        bootstrapper.Apply(services);

        Assert.That(spy.Calls, Has.Count.EqualTo(2));
        Assert.That(spy.Calls.All(c => c.Configuration.ProviderType == providerType), Is.True);
        Assert.That(spy.Calls.All(c => c.ServiceType == typeof(object)), Is.True);
        Assert.That(spy.Calls.Any(c => c.SlotName == "slot1" && c.Configuration.Settings.ContainsKey("a") && c.Configuration.Settings["a"] == "1"), Is.True);
        Assert.That(spy.Calls.Any(c => c.SlotName == "slot2" && c.Configuration.Settings.ContainsKey("b") && c.Configuration.Settings["b"] == "2"), Is.True);
    }

    [Test]
    public void Apply_WithUnknownProviderType_ThrowsKeyNotFoundException()
    {
        var unknownType = $"unknown-{Guid.NewGuid()}";
        using var keyPair = new EphemeralKeyPair();

        var slot = EncryptSlot(unknownType, new Dictionary<string, string>(), keyPair.PublicKeyBase64);
        var slots = new Dictionary<string, EncryptedSlotConfiguration> { ["s1"] = slot };
        var response = new WorkflowConfigurationResponse(Guid.NewGuid(), true, null, slots);

        var slotDefinitions = new List<SlotDefinition>
        {
            new("s1", null) { ServiceType = typeof(object) }
        };

        var bootstrapper = new WorkflowBootstrapper(response, keyPair, new SlotHandlerResolver(), slotDefinitions);

        Assert.Throws<KeyNotFoundException>(() => bootstrapper.Apply(new ServiceCollection()));
    }

    [Test]
    public void Apply_WithMissingSlotDefinition_UsesObjectTypeAndThrowsKeyNotFoundForUnregisteredProvider()
    {
        // When a slot has no SlotDefinition, serviceType defaults to typeof(object)
        // and the resolver is consulted; if the provider type is unregistered it throws KeyNotFoundException.
        var providerType = $"bt-provider-{Guid.NewGuid()}";
        using var keyPair = new EphemeralKeyPair();

        var slot = EncryptSlot(providerType, new Dictionary<string, string>(), keyPair.PublicKeyBase64);
        var slots = new Dictionary<string, EncryptedSlotConfiguration> { ["missing"] = slot };
        var response = new WorkflowConfigurationResponse(Guid.NewGuid(), true, null, slots);

        var bootstrapper = new WorkflowBootstrapper(response, keyPair, new SlotHandlerResolver(), []);

        Assert.Throws<KeyNotFoundException>(() => bootstrapper.Apply(new ServiceCollection()));
    }

    private sealed class SpySlotHandler : ISlotHandler
    {
        public List<(string SlotName, Type ServiceType, SlotConfiguration Configuration)> Calls { get; } = new();

        public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
            => Calls.Add((slotName, serviceType, configuration));
    }
}

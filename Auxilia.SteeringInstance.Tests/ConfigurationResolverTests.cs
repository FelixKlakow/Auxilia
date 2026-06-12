using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.SteeringInstance.Tests;

[TestFixture]
public class ConfigurationResolverTests
{
    private SlotConfigurationStore _store = null!;
    private ConfigurationResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _store = TestStores.NewSlotConfigurationStore();
        _resolver = new ConfigurationResolver(_store, TestStores.NewSignalHandlerStore(), NullLogger<ConfigurationResolver>.Instance);
    }

    [Test]
    public async Task Resolve_NoConfigurations_ReturnsFailure()
    {
        var result = await _resolver.ResolveAsync("TestWorkflow", ValidPublicKey());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureReason, Is.Not.Null);
    }

    [Test]
    public async Task Resolve_DirtyConfiguration_ReturnsFailure()
    {
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Dirty));

        var result = await _resolver.ResolveAsync("TestWorkflow", ValidPublicKey());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureReason, Does.Contain("dirty"));
    }

    [Test]
    public async Task Resolve_AllValidConfigurations_ReturnsSuccessWithEncryptedSlots()
    {
        using var rsa = RSA.Create(4096);
        var publicKey = Convert.ToBase64String(
            rsa.ExportSubjectPublicKeyInfo());

        Dictionary<string, string> settingsA = new() { ["ApiKey"] = "secret1" };
        Dictionary<string, string> settingsB = new() { ["Token"] = "secret2" };
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderA", settingsA,
                ConfigurationStatus.Valid));
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotB", "ProviderB", settingsB,
                ConfigurationStatus.Valid));

        var result = await _resolver.ResolveAsync("TestWorkflow", publicKey);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Slots, Has.Count.EqualTo(2));

        // Ciphertext is non-empty
        Assert.That(result.Slots["slotA"].EncryptedSettings, Is.Not.Empty);
        Assert.That(result.Slots["slotB"].EncryptedSettings, Is.Not.Empty);

        // Ciphertexts are distinct per slot
        Assert.That(result.Slots["slotA"].EncryptedSettings,
            Is.Not.EqualTo(result.Slots["slotB"].EncryptedSettings));
    }

    [Test]
    public async Task Resolve_AllValidConfigurations_CipherCanBeDecryptedToOriginalSettings()
    {
        using var rsa = RSA.Create(4096);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        Dictionary<string, string> settings = new() { ["ApiKey"] = "super-secret" };
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX", settings,
                ConfigurationStatus.Valid));

        var result = await _resolver.ResolveAsync("TestWorkflow", publicKey);
        Assert.That(result.IsSuccess, Is.True);

        // Decrypt using the private key
        var cipherBytes = Convert.FromBase64String(result.Slots["slotA"].EncryptedSettings);
        var plainBytes = rsa.Decrypt(cipherBytes, RSAEncryptionPadding.OaepSHA256);
        var plainJson = System.Text.Encoding.UTF8.GetString(plainBytes);

        var decoded = JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson);
        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!["ApiKey"], Is.EqualTo("super-secret"));
    }

    private static string ValidPublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.SteeringInstance.Tests;

[TestFixture]
[Category("Unit")]
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

    // ------------------------------------------------------------------ ValidateConfiguredAsync

    [Test]
    public async Task ValidateConfigured_NoConfigurations_ReturnsInvalid()
    {
        var (isValid, reason) = await _resolver.ValidateConfiguredAsync("TestWorkflow");

        Assert.That(isValid, Is.False);
        Assert.That(reason, Is.Not.Null);
    }

    [Test]
    public async Task ValidateConfigured_DirtyConfiguration_ReturnsInvalid()
    {
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Dirty));

        var (isValid, reason) = await _resolver.ValidateConfiguredAsync("TestWorkflow");

        Assert.That(isValid, Is.False);
        Assert.That(reason, Does.Contain("dirty"));
    }

    [Test]
    public async Task ValidateConfigured_AllValidConfigurations_ReturnsValid()
    {
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderA",
                new Dictionary<string, string> { ["ApiKey"] = "secret1" },
                ConfigurationStatus.Valid));
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotB", "ProviderB",
                new Dictionary<string, string> { ["Token"] = "secret2" },
                ConfigurationStatus.Valid));

        var (isValid, reason) = await _resolver.ValidateConfiguredAsync("TestWorkflow");

        Assert.That(isValid, Is.True);
        Assert.That(reason, Is.Null);
    }

    // ------------------------------------------------------------------ ResolveSlotAsync

    [Test]
    public async Task ResolveSlot_UnknownSlot_ReturnsError()
    {
        var (success, error, slot) = await _resolver.ResolveSlotAsync(
            "TestWorkflow", "slotA", ValidPublicKey());

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("slotA"));
        Assert.That(slot, Is.Null);
    }

    [Test]
    public async Task ResolveSlot_DirtyConfiguration_ReturnsError()
    {
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Dirty));

        var (success, error, slot) = await _resolver.ResolveSlotAsync(
            "TestWorkflow", "slotA", ValidPublicKey());

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("dirty"));
        Assert.That(slot, Is.Null);
    }

    [Test]
    public async Task ResolveSlot_HappyPath_ReturnsEncryptedSlotWithProviderType()
    {
        using var rsa = RSA.Create(4096);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderA",
                new Dictionary<string, string> { ["ApiKey"] = "secret1" },
                ConfigurationStatus.Valid));

        var (success, error, slot) = await _resolver.ResolveSlotAsync(
            "TestWorkflow", "slotA", publicKey);

        Assert.That(success, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(slot, Is.Not.Null);
        Assert.That(slot!.ProviderType, Is.EqualTo("ProviderA"));
        Assert.That(slot.EncryptedSettings, Is.Not.Empty);
    }

    [Test]
    public async Task ResolveSlot_HappyPath_CipherCanBeDecryptedToOriginalSettings()
    {
        using var rsa = RSA.Create(4096);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        Dictionary<string, string> settings = new() { ["ApiKey"] = "super-secret" };
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX", settings,
                ConfigurationStatus.Valid));

        var (success, _, slot) = await _resolver.ResolveSlotAsync(
            "TestWorkflow", "slotA", publicKey);
        Assert.That(success, Is.True);

        // Decrypt using the private key
        var cipherBytes = Convert.FromBase64String(slot!.EncryptedSettings);
        var plainBytes = rsa.Decrypt(cipherBytes, RSAEncryptionPadding.OaepSHA256);
        var plainJson = System.Text.Encoding.UTF8.GetString(plainBytes);

        var decoded = JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson);
        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!["ApiKey"], Is.EqualTo("super-secret"));
    }

    [Test]
    public async Task ResolveSlot_TwoSlots_ProduceDistinctCiphertexts()
    {
        using var rsa = RSA.Create(4096);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderA",
                new Dictionary<string, string> { ["ApiKey"] = "secret1" },
                ConfigurationStatus.Valid));
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotB", "ProviderB",
                new Dictionary<string, string> { ["Token"] = "secret2" },
                ConfigurationStatus.Valid));

        var (_, _, slotA) = await _resolver.ResolveSlotAsync("TestWorkflow", "slotA", publicKey);
        var (_, _, slotB) = await _resolver.ResolveSlotAsync("TestWorkflow", "slotB", publicKey);

        Assert.That(slotA!.EncryptedSettings, Is.Not.EqualTo(slotB!.EncryptedSettings));
    }

    [Test]
    public async Task ResolveSlot_PublicKeyNotBase64_ReturnsError()
    {
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Valid));

        var (success, error, slot) = await _resolver.ResolveSlotAsync(
            "TestWorkflow", "slotA", "not-base64!!!");

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("base-64"));
        Assert.That(slot, Is.Null);
    }

    [Test]
    public async Task ResolveSlot_PublicKeyNotValidDer_ReturnsError()
    {
        await _store.UpsertConfigurationAsync("TestWorkflow",
            new StoredSlotConfiguration("slotA", "ProviderX",
                new Dictionary<string, string> { ["key"] = "val" },
                ConfigurationStatus.Valid));

        var (success, error, slot) = await _resolver.ResolveSlotAsync(
            "TestWorkflow", "slotA", Convert.ToBase64String([1, 2, 3, 4]));

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("RSA public key"));
        Assert.That(slot, Is.Null);
    }

    private static string ValidPublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }
}

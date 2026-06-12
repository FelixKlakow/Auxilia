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
    private WorkflowConfigurationStore _configurationStore = null!;
    private ConfigurationResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _store = TestStores.NewSlotConfigurationStore();
        _configurationStore = TestStores.NewWorkflowConfigurationStore();
        _resolver = new ConfigurationResolver(
            _store, TestStores.NewSignalHandlerStore(), _configurationStore,
            NullLogger<ConfigurationResolver>.Instance);
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

    // ------------------------------------------------------------------ Named workflow configurations (#18)

    private Task<StoredWorkflowConfiguration> SeedConfigurationAsync(
        bool enabled = true, IReadOnlyList<StoredSlotBinding>? bindings = null)
        => _configurationStore.UpsertAsync(new StoredWorkflowConfiguration(
            "alpha", "Alpha", "TestWorkflow", "docker://test-wf:1", enabled,
            bindings ?? [new StoredSlotBinding("slotA", "ConfigProvider",
                new Dictionary<string, string> { ["ApiKey"] = "from-config" })]));

    [Test]
    public async Task ValidateConfiguration_Missing_ReturnsInvalid()
    {
        var (isValid, reason) = await _resolver.ValidateConfigurationAsync(Guid.NewGuid());

        Assert.That(isValid, Is.False);
        Assert.That(reason, Does.Contain("not found"));
    }

    [Test]
    public async Task ValidateConfiguration_Disabled_ReturnsInvalid()
    {
        var configuration = await SeedConfigurationAsync(enabled: false);

        var (isValid, reason) = await _resolver.ValidateConfigurationAsync(configuration.Id);

        Assert.That(isValid, Is.False);
        Assert.That(reason, Does.Contain("disabled"));
    }

    [Test]
    public async Task ValidateConfiguration_NoBindings_ReturnsInvalid()
    {
        var configuration = await SeedConfigurationAsync(bindings: []);

        var (isValid, reason) = await _resolver.ValidateConfigurationAsync(configuration.Id);

        Assert.That(isValid, Is.False);
        Assert.That(reason, Does.Contain("no slot bindings"));
    }

    [Test]
    public async Task ValidateConfiguration_EnabledWithBindings_ReturnsValid()
    {
        var configuration = await SeedConfigurationAsync();

        var (isValid, reason) = await _resolver.ValidateConfigurationAsync(configuration.Id);

        Assert.That(isValid, Is.True);
        Assert.That(reason, Is.Null);
    }

    [Test]
    public async Task ResolveConfigurationSlot_HappyPath_DecryptsToBindingSettings()
    {
        using var rsa = RSA.Create(4096);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var configuration = await SeedConfigurationAsync();

        var (success, error, slot) = await _resolver.ResolveConfigurationSlotAsync(
            configuration.Id, "slotA", publicKey);

        Assert.That(success, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(slot!.ProviderType, Is.EqualTo("ConfigProvider"));

        var plainJson = System.Text.Encoding.UTF8.GetString(
            rsa.Decrypt(Convert.FromBase64String(slot.EncryptedSettings), RSAEncryptionPadding.OaepSHA256));
        var decoded = JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson);
        Assert.That(decoded!["ApiKey"], Is.EqualTo("from-config"));
    }

    [Test]
    public async Task ResolveConfigurationSlot_MissingConfiguration_ReturnsError()
    {
        var (success, error, slot) = await _resolver.ResolveConfigurationSlotAsync(
            Guid.NewGuid(), "slotA", ValidPublicKey());

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("not found"));
        Assert.That(slot, Is.Null);
    }

    [Test]
    public async Task ResolveConfigurationSlot_DisabledConfiguration_ReturnsError()
    {
        var configuration = await SeedConfigurationAsync(enabled: false);

        var (success, error, slot) = await _resolver.ResolveConfigurationSlotAsync(
            configuration.Id, "slotA", ValidPublicKey());

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("disabled"));
        Assert.That(slot, Is.Null);
    }

    [Test]
    public async Task ResolveConfigurationSlot_UnboundSlot_ReturnsError()
    {
        var configuration = await SeedConfigurationAsync();

        var (success, error, slot) = await _resolver.ResolveConfigurationSlotAsync(
            configuration.Id, "ghost-slot", ValidPublicKey());

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("ghost-slot"));
        Assert.That(slot, Is.Null);
    }

    private static string ValidPublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }
}

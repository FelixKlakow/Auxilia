using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.SteeringInstance.Tests.ComponentTests;

/// <summary>
/// Proves the Steering Instance's platform state survives a restart: two successive
/// service providers ("processes") over the same JSON-backend directory must see the
/// state the previous one wrote — schemas, slot configurations (decrypted correctly),
/// providers, and run lifecycle records.
/// </summary>
[TestFixture]
[Category("Component")]
public class PlatformStatePersistenceComponentTests
{
    private string _dataDir = null!;
    private PlatformDataSettings _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"auxilia-persist-{Guid.NewGuid():N}");
        _settings = new PlatformDataSettings
        {
            Backend = PlatformDataBackend.Json,
            JsonDirectory = _dataDir,
            ProtectionKeyBase64 = Convert.ToBase64String(new byte[32]) // fixed key across "restarts"
        };
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private ServiceProvider BuildHost()
    {
        var services = new ServiceCollection();
        services.AddPlatformEntity<WorkflowSchemaRecord>(_settings);
        services.AddPlatformEntity<SlotConfigurationRecord>(_settings);
        services.AddPlatformEntity<SlotProviderRecord>(_settings);
        services.AddPlatformEntity<WorkflowInstanceRecord>(_settings);
        services.AddSettingsProtection(_settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WorkflowSchemaStore>();
        services.AddSingleton<SlotConfigurationStore>();
        services.AddSingleton<SlotProviderRegistry>();
        services.AddSingleton<WorkflowInstanceRegistry>();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task SlotConfiguration_SurvivesRestart_AndDecryptsWithSameKey()
    {
        var instanceId = Guid.NewGuid();

        await using (var firstRun = BuildHost())
        {
            await firstRun.GetRequiredService<SlotConfigurationStore>().UpsertConfigurationAsync(
                "code-review",
                new StoredSlotConfiguration("repository", "git-provider",
                    new Dictionary<string, string> { ["Token"] = "super-secret" },
                    ConfigurationStatus.Valid));
            await firstRun.GetRequiredService<SlotProviderRegistry>()
                .UpsertAsync("git-provider", "/plugins/git.slothandler.dll");
            await firstRun.GetRequiredService<WorkflowInstanceRegistry>()
                .RegisterAsync(instanceId, "code-review");
        }

        // Secrets must not be readable in the raw persisted file.
        var rawJson = await File.ReadAllTextAsync(
            Path.Combine(_dataDir, $"{nameof(SlotConfigurationRecord)}.json"));
        Assert.That(rawJson, Does.Not.Contain("super-secret"),
            "Slot settings must be encrypted at rest.");

        await using (var secondRun = BuildHost())
        {
            var configs = await secondRun.GetRequiredService<SlotConfigurationStore>()
                .GetConfigurationsAsync("code-review");
            Assert.That(configs, Has.Count.EqualTo(1));
            Assert.That(configs[0].Settings["Token"], Is.EqualTo("super-secret"));
            Assert.That(configs[0].Status, Is.EqualTo(ConfigurationStatus.Valid));

            Assert.That(await secondRun.GetRequiredService<SlotProviderRegistry>()
                .GetDllPathAsync("git-provider"), Is.EqualTo("/plugins/git.slothandler.dll"));

            Assert.That(await secondRun.GetRequiredService<WorkflowInstanceRegistry>()
                .GetWorkflowTypeAsync(instanceId), Is.EqualTo("code-review"));
        }
    }

    [Test]
    public async Task SlotConfiguration_AfterRestartWithDifferentKey_FailsToDecrypt()
    {
        await using (var firstRun = BuildHost())
        {
            await firstRun.GetRequiredService<SlotConfigurationStore>().UpsertConfigurationAsync(
                "code-review",
                new StoredSlotConfiguration("repository", "git-provider",
                    new Dictionary<string, string> { ["Token"] = "super-secret" },
                    ConfigurationStatus.Valid));
        }

        _settings.ProtectionKeyBase64 = Convert.ToBase64String(
            Enumerable.Repeat((byte)0xAB, 32).ToArray());

        await using var secondRun = BuildHost();
        var store = secondRun.GetRequiredService<SlotConfigurationStore>();
        Assert.ThrowsAsync<System.Security.Cryptography.AuthenticationTagMismatchException>(
            () => store.GetConfigurationsAsync("code-review"));
    }
}

using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Runner.Tests.ComponentTests;

/// <summary>
/// Proves the runner's platform state survives a restart: two successive service providers
/// ("processes") over the same JSON-backend directory must see the state the previous one wrote.
/// The runner no longer stores slot credentials (the Core resolves them just-in-time), so this
/// covers the durable state it does own — registered slot providers and run lifecycle records.
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
            JsonDirectory = _dataDir
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
        services.AddPlatformEntity<SlotProviderRecord>(_settings);
        services.AddPlatformEntity<WorkflowInstanceRecord>(_settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SlotProviderRegistry>();
        services.AddSingleton<WorkflowInstanceRegistry>();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task PlatformState_SurvivesRestart()
    {
        var instanceId = Guid.NewGuid();

        await using (var firstRun = BuildHost())
        {
            await firstRun.GetRequiredService<SlotProviderRegistry>()
                .UpsertAsync("git-provider", "/plugins/git.slothandler.dll");
            await firstRun.GetRequiredService<WorkflowInstanceRegistry>()
                .RegisterAsync(instanceId, "code-review");
        }

        await using (var secondRun = BuildHost())
        {
            Assert.That(await secondRun.GetRequiredService<SlotProviderRegistry>()
                .GetDllPathAsync("git-provider"), Is.EqualTo("/plugins/git.slothandler.dll"),
                "A registered slot provider must survive a restart.");

            Assert.That(await secondRun.GetRequiredService<WorkflowInstanceRegistry>()
                .GetWorkflowTypeAsync(instanceId), Is.EqualTo("code-review"),
                "A run lifecycle record must survive a restart.");
        }
    }
}

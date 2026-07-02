using System.Text.Json;
using Auxilia.BackendService.Dashboard;
using Auxilia.Governance;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class DashboardStatsServiceTests
{
    private IDataAccess<WorkflowInstanceRecord> _instances = null!;
    private IDataAccess<WorkflowConfigurationRecord> _configurations = null!;
    private IDataAccess<SlotInstanceRecord> _slotInstances = null!;
    private IDataAccess<ScheduledTriggerRecord> _schedules = null!;
    private IDataAccess<MailboxTriggerRecord> _mailboxTriggers = null!;
    private IDataAccess<ArtifactTriggerRecord> _artifactTriggers = null!;
    private IDataAccess<SlotProviderRecord> _providers = null!;
    private IDataAccess<ProviderCatalogRecord> _catalog = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private DashboardStatsService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _instances = new InMemoryDataAccess<WorkflowInstanceRecord>();
        _configurations = new InMemoryDataAccess<WorkflowConfigurationRecord>();
        _slotInstances = new InMemoryDataAccess<SlotInstanceRecord>();
        _schedules = new InMemoryDataAccess<ScheduledTriggerRecord>();
        _mailboxTriggers = new InMemoryDataAccess<MailboxTriggerRecord>();
        _artifactTriggers = new InMemoryDataAccess<ArtifactTriggerRecord>();
        _providers = new InMemoryDataAccess<SlotProviderRecord>();
        _catalog = new InMemoryDataAccess<ProviderCatalogRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _sut = new DashboardStatsService(
            _instances, _configurations, _slotInstances, _schedules, _mailboxTriggers,
            _artifactTriggers,
            new ProviderCatalogService(_providers, _catalog, new AuditLog(_audit, TimeProvider.System)),
            TimeProvider.System);
    }

    [TearDown]
    public void TearDown()
    {
        (_instances as IDisposable)?.Dispose();
        (_configurations as IDisposable)?.Dispose();
        (_slotInstances as IDisposable)?.Dispose();
        (_schedules as IDisposable)?.Dispose();
        (_mailboxTriggers as IDisposable)?.Dispose();
        (_artifactTriggers as IDisposable)?.Dispose();
        (_providers as IDisposable)?.Dispose();
        (_catalog as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private Task SeedRunAsync(string state, TimeSpan age, TimeSpan? duration = null)
        => _instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "pull-request-code-review",
            State = state,
            CreatedUtc = DateTimeOffset.UtcNow - age,
            CompletedUtc = state is "Running" or "Queued" ? null : DateTimeOffset.UtcNow - age + (duration ?? TimeSpan.FromMinutes(1))
        });

    private Task SeedConfigurationAsync(
        string name = "team-review", bool enabled = true, Guid? boundInstanceId = null)
        => _configurations.SaveAsync(new WorkflowConfigurationRecord
        {
            Id = WorkflowConfigurationRecord.IdFor(name),
            Name = name,
            DisplayName = name,
            WorkflowType = "pull-request-code-review",
            PackageUri = "docker://review:1",
            Enabled = enabled,
            SlotBindingsJson = JsonSerializer.Serialize(new List<WorkflowConfigurationSlotBinding>
            {
                new()
                {
                    SlotName = "work-items",
                    ProviderType = "email-work-items",
                    ProtectedSettingsJson = "{}",
                    SlotInstanceId = boundInstanceId
                }
            })
        });

    [Test]
    public async Task Get_ComputesTheLast24hNumbers()
    {
        await SeedRunAsync("Success", TimeSpan.FromHours(1));
        await SeedRunAsync("Success", TimeSpan.FromHours(2));
        await SeedRunAsync("Failed", TimeSpan.FromHours(3));
        await SeedRunAsync("PreFlightFailed", TimeSpan.FromHours(4));
        await SeedRunAsync("Running", TimeSpan.FromMinutes(5));
        await SeedRunAsync("Success", TimeSpan.FromHours(30)); // outside the window
        await SeedConfigurationAsync("enabled-config");
        await SeedConfigurationAsync("disabled-config", enabled: false);

        var stats = await _sut.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stats.Runs24h, Is.EqualTo(5), "the 30 h old run is outside the window");
            Assert.That(stats.Failures24h, Is.EqualTo(2), "Failed and PreFlightFailed both count");
            Assert.That(stats.SuccessRatePercent, Is.EqualTo(50), "2 of 4 terminal runs in the window succeeded");
            Assert.That(stats.EnabledConfigurations, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Get_WithNoTerminalRuns_HasNoSuccessRate()
    {
        await SeedRunAsync("Running", TimeSpan.FromMinutes(5));

        var stats = await _sut.GetAsync();

        Assert.That(stats.SuccessRatePercent, Is.Null);
    }

    [Test]
    public async Task Get_RecentRuns_AreNewestFirst_CappedAtEight()
    {
        for (var i = 0; i < 10; i++)
            await SeedRunAsync("Success", TimeSpan.FromMinutes(i + 1));

        var stats = await _sut.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stats.RecentRuns, Has.Count.EqualTo(8));
            Assert.That(stats.RecentRuns, Is.Ordered.Descending.By(nameof(WorkflowInstanceRecord.CreatedUtc)));
        });
    }

    [Test]
    public async Task Get_FlagsConfigurationsReferencingDeletedSlotInstances()
    {
        await SeedConfigurationAsync("broken", boundInstanceId: Guid.NewGuid()); // instance never seeded
        var live = new SlotInstanceRecord
        {
            Id = SlotInstanceRecord.IdFor("team-mailbox"),
            Name = "team-mailbox",
            DisplayName = "Team mailbox",
            ProviderType = "email-work-items",
            ProtectedSettingsJson = "{}"
        };
        await _slotInstances.SaveAsync(live);
        await SeedConfigurationAsync("healthy", boundInstanceId: live.Id);

        var stats = await _sut.GetAsync();

        var item = stats.Attention.Single(a => a.Text.Contains("deleted slot instance"));
        Assert.Multiple(() =>
        {
            Assert.That(item.Text, Does.Contain("broken"));
            Assert.That(item.Href, Is.EqualTo($"workflows/{WorkflowConfigurationRecord.IdFor("broken")}/edit"));
        });
    }

    [Test]
    public async Task Get_FlagsRecentFailures()
    {
        await SeedRunAsync("Failed", TimeSpan.FromHours(1));

        var stats = await _sut.GetAsync();

        Assert.That(stats.Attention.Single(a => a.Href == "runs").Text, Does.Contain("1 run failed"));
    }

    [Test]
    public async Task Get_OnAFreshSystem_EveryGettingStartedStepIsOpen()
    {
        var stats = await _sut.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stats.SetupComplete, Is.False);
            Assert.That(stats.GettingStarted, Has.Count.EqualTo(5));
            Assert.That(stats.GettingStarted.All(s => !s.Done), Is.True);
        });
    }

    [Test]
    public async Task Get_OnAFullySetUpSystem_TheChecklistIsComplete()
    {
        await _providers.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor("email-work-items"),
            ProviderType = "email-work-items",
            DllPath = "plugins/email.slothandler.dll"
        });
        await _catalog.SaveAsync(new ProviderCatalogRecord
        {
            Id = ProviderCatalogRecord.IdFor("email-work-items"),
            ProviderType = "email-work-items",
            Available = true
        });
        await _slotInstances.SaveAsync(new SlotInstanceRecord
        {
            Id = SlotInstanceRecord.IdFor("team-mailbox"),
            Name = "team-mailbox",
            DisplayName = "Team mailbox",
            ProviderType = "email-work-items",
            ProtectedSettingsJson = "{}"
        });
        await SeedConfigurationAsync();
        await _schedules.SaveAsync(new ScheduledTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "pull-request-code-review",
            WorkflowPackageUri = "docker://review:1",
            IntervalSeconds = 3600
        });
        await SeedRunAsync("Success", TimeSpan.FromHours(1));

        var stats = await _sut.GetAsync();

        Assert.That(stats.SetupComplete, Is.True,
            () => string.Join(", ", stats.GettingStarted.Where(s => !s.Done).Select(s => s.Title)));
    }
}

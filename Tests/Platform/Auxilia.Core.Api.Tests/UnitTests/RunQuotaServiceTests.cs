using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Run-API quotas: the platform-wide active-run cap and the per-principal dispatch rate.
/// Unset quotas (the default) never reject; system dispatches without a principal are exempt
/// from the per-principal rate; every rejection is audited.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunQuotaServiceTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private InMemoryDataAccess<CoreRunRecord> _runs = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;
    private ManualClock _clock = null!;
    private CoreApiSettings _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _runs = new InMemoryDataAccess<CoreRunRecord>();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _clock = new ManualClock();
        _settings = new CoreApiSettings();
    }

    [TearDown]
    public void TearDown()
    {
        _runs.Dispose();
        _auditRecords.Dispose();
    }

    private RunQuotaService NewService() => new(
        _runs, new AuditLog(_auditRecords, _clock), _clock, Options.Create(_settings));

    private Task AddRunAsync(string state) => _runs.SaveAsync(new CoreRunRecord
    {
        Id = Guid.NewGuid(), WorkflowType = "wf", State = state
    }, CancellationToken.None);

    [Test]
    public async Task Defaults_AreUnlimited()
    {
        for (var i = 0; i < 50; i++)
            await AddRunAsync("Running");
        var service = NewService();

        for (var i = 0; i < 100; i++)
            await service.EnsureCanDispatchAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Pass("no quota, no rejection");
    }

    [Test]
    public async Task ActiveRunCap_RejectsAtTheCap_TerminalRunsDoNotCount()
    {
        _settings.RunQuotas.MaxActiveRuns = 2;
        await AddRunAsync("Running");
        await AddRunAsync("Success");
        await AddRunAsync("Failed");
        var service = NewService();

        // 1 active of 2 allowed — passes; a second active run then hits the cap.
        await service.EnsureCanDispatchAsync(null, CancellationToken.None);
        await AddRunAsync("Dispatched");

        var ex = Assert.ThrowsAsync<RunQuotaExceededException>(
            () => service.EnsureCanDispatchAsync(null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("active-run cap"));
        Assert.That((await _auditRecords.ReadAsync(CancellationToken.None))
            .Count(a => a.Action == "run.quota-exceeded"), Is.EqualTo(1));
    }

    [Test]
    public async Task DispatchRate_FixedWindowPerPrincipal_ResetsAfterAMinute()
    {
        _settings.RunQuotas.MaxDispatchesPerPrincipalPerMinute = 2;
        var principal = Guid.NewGuid();
        var service = NewService();

        await service.EnsureCanDispatchAsync(principal, CancellationToken.None);
        await service.EnsureCanDispatchAsync(principal, CancellationToken.None);
        Assert.ThrowsAsync<RunQuotaExceededException>(
            () => service.EnsureCanDispatchAsync(principal, CancellationToken.None));

        // Another principal has its own window.
        await service.EnsureCanDispatchAsync(Guid.NewGuid(), CancellationToken.None);

        // The window resets after a minute.
        _clock.Now += TimeSpan.FromSeconds(61);
        await service.EnsureCanDispatchAsync(principal, CancellationToken.None);
    }

    [Test]
    public async Task DispatchRate_SystemDispatchesWithoutPrincipal_AreExempt()
    {
        _settings.RunQuotas.MaxDispatchesPerPrincipalPerMinute = 1;
        var service = NewService();

        for (var i = 0; i < 5; i++)
            await service.EnsureCanDispatchAsync(null, CancellationToken.None);

        Assert.Pass("failover redispatch and pipeline runs must never be rate-limited");
    }
}

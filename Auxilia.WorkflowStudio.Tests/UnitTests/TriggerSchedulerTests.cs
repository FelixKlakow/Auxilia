using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.WorkflowStudio.Triggers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.WorkflowStudio.Tests.UnitTests;

/// <summary>
/// The Studio interval scheduler dispatches due triggers through the Core Run API (not the bus):
/// configuration-wired triggers via <c>RunConfigurationAsync(onBehalfOf, context)</c>, inline ones
/// via <c>RunAsync</c>, stamping <c>LastDispatchedUtc</c> so a trigger is not re-fired every scan.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class TriggerSchedulerTests
{
    private static (TriggerScheduler Scheduler, FakeCoreClient Core, InMemoryDataAccess<ScheduledTriggerRecord> Triggers)
        New(TimeProvider? clock = null)
    {
        var triggers = new InMemoryDataAccess<ScheduledTriggerRecord>();
        var core = new FakeCoreClient();
        var audit = new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System);
        var scheduler = new TriggerScheduler(
            triggers, core, audit, clock ?? TimeProvider.System,
            Options.Create(new TriggerSettings()), NullLogger<TriggerScheduler>.Instance);
        return (scheduler, core, triggers);
    }

    [Test]
    public async Task ConfigurationTrigger_DispatchesViaRunConfiguration_WithOnBehalfOfAndContext()
    {
        var (scheduler, core, triggers) = New();
        var configId = Guid.NewGuid();
        var principal = Guid.NewGuid();
        await triggers.SaveAsync(new ScheduledTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "codereview",
            IntervalSeconds = 60,
            RunAsPrincipalId = principal,
            WorkflowConfigurationId = configId,
            ContextJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["K"] = "V" })
        }, CancellationToken.None);

        await scheduler.DispatchDueTriggersAsync(CancellationToken.None);

        Assert.That(core.RunConfigurationIds.Single(), Is.EqualTo(configId));
        Assert.That(core.RunConfigurationOnBehalfOf.Single(), Is.EqualTo(principal));
        Assert.That(core.RunConfigurationContexts.Single()!["K"], Is.EqualTo("V"));
        Assert.That(core.RunRequests, Is.Empty, "A configuration-wired trigger must not use the inline path.");
    }

    [Test]
    public async Task InlineTrigger_DispatchesViaRunAsync_WithRequestedByAndContext()
    {
        var (scheduler, core, triggers) = New();
        var principal = Guid.NewGuid();
        await triggers.SaveAsync(new ScheduledTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "codereview",
            IntervalSeconds = 60,
            RunAsPrincipalId = principal,
            WorkflowConfigurationId = null,
            ContextJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["K"] = "V" })
        }, CancellationToken.None);

        await scheduler.DispatchDueTriggersAsync(CancellationToken.None);

        var request = core.RunRequests.Single();
        Assert.That(request.WorkflowType, Is.EqualTo("codereview"));
        Assert.That(request.RequestedBy, Is.EqualTo(principal));
        Assert.That(request.Context!["K"], Is.EqualTo("V"));
        Assert.That(core.RunConfigurationIds, Is.Empty);
    }

    [Test]
    public async Task DisabledTrigger_IsNotDispatched()
    {
        var (scheduler, core, triggers) = New();
        await triggers.SaveAsync(new ScheduledTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "wt",
            IntervalSeconds = 60,
            Enabled = false,
            WorkflowConfigurationId = Guid.NewGuid()
        }, CancellationToken.None);

        await scheduler.DispatchDueTriggersAsync(CancellationToken.None);

        Assert.That(core.RunConfigurationIds, Is.Empty);
        Assert.That(core.RunRequests, Is.Empty);
    }

    [Test]
    public async Task Dispatch_StampsLastDispatched_SoTheTriggerDoesNotRefireImmediately()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);
        var (scheduler, core, triggers) = New(clock);
        await triggers.SaveAsync(new ScheduledTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "wt",
            IntervalSeconds = 3600,
            WorkflowConfigurationId = Guid.NewGuid()
        }, CancellationToken.None);

        await scheduler.DispatchDueTriggersAsync(CancellationToken.None);
        await scheduler.DispatchDueTriggersAsync(CancellationToken.None);

        Assert.That(core.RunConfigurationIds, Has.Count.EqualTo(1),
            "The interval has not elapsed, so the second scan must not re-dispatch.");
        var stored = (await triggers.ReadAsync(CancellationToken.None)).Single();
        Assert.That(stored.LastDispatchedUtc, Is.EqualTo(now));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

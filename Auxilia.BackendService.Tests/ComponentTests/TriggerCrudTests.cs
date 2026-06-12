using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Trigger CRUD round-trips through the real DI container: records land in the platform
/// data layer the scheduler / artifact handler consume, mutations are audited, and the
/// operator pages render the stored triggers.
/// </summary>
[TestFixture]
[Category("Component")]
public class TriggerCrudTests : DashboardComponentTestBase
{
    private TriggerAdministration Triggers => Factory.Services.GetRequiredService<TriggerAdministration>();

    [Test]
    public async Task ScheduledTrigger_CrudRoundTrip_PersistsAndAudits()
    {
        var created = await Triggers.SaveScheduledAsync("test-operator", new ScheduledTriggerRecord
        {
            WorkflowType = "component-test-schedule",
            WorkflowPackageUri = "docker://scan:1",
            IntervalSeconds = 300,
            ContextJson = """{"WorkItemId":"42"}"""
        });

        var updated = await Triggers.SaveScheduledAsync("test-operator", created with { IntervalSeconds = 600 });
        var listed = await Triggers.ListScheduledAsync();

        Assert.Multiple(() =>
        {
            Assert.That(updated.Id, Is.EqualTo(created.Id));
            Assert.That(listed.Single(t => t.Id == created.Id).IntervalSeconds, Is.EqualTo(600));
        });

        Assert.That(await Triggers.DeleteScheduledAsync("test-operator", created.Id), Is.True);
        Assert.That(await Triggers.ListScheduledAsync(), Has.None.Matches<ScheduledTriggerRecord>(
            t => t.Id == created.Id));

        var audit = await Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>().ReadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(audit.Count(a => a.Action == "trigger.scheduled.created"), Is.EqualTo(1));
            Assert.That(audit.Count(a => a.Action == "trigger.scheduled.updated"), Is.EqualTo(1));
            Assert.That(audit.Count(a => a.Action == "trigger.scheduled.deleted"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ArtifactTrigger_CrudRoundTrip_PersistsAndAudits()
    {
        var created = await Triggers.SaveArtifactAsync("test-operator", new ArtifactTriggerRecord
        {
            ArtifactType = "component-test-report",
            WorkflowType = "follow-up-workflow",
            WorkflowPackageUri = "docker://fix:1"
        });

        Assert.That(created.Id,
            Is.EqualTo(ArtifactTriggerRecord.IdFor("component-test-report", "follow-up-workflow")));

        var disabled = await Triggers.SaveArtifactAsync("test-operator", created with { Enabled = false });
        Assert.Multiple(async () =>
        {
            Assert.That(disabled.Id, Is.EqualTo(created.Id));
            Assert.That((await Triggers.ListArtifactAsync()).Single(t => t.Id == created.Id).Enabled, Is.False);
        });

        Assert.That(await Triggers.DeleteArtifactAsync("test-operator", created.Id), Is.True);

        var audit = await Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>().ReadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(audit.Count(a => a.Action == "trigger.artifact.created"), Is.EqualTo(1));
            Assert.That(audit.Count(a => a.Action == "trigger.artifact.updated"), Is.EqualTo(1));
            Assert.That(audit.Count(a => a.Action == "trigger.artifact.deleted"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SchedulesPage_AsOperator_RendersStoredTrigger()
    {
        var trigger = await Triggers.SaveScheduledAsync("test-operator", new ScheduledTriggerRecord
        {
            WorkflowType = "rendered-schedule-workflow",
            WorkflowPackageUri = "docker://scan:1",
            IntervalSeconds = 60
        });

        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, "/operator/schedules", cookie);

        Assert.That(html, Does.Contain("rendered-schedule-workflow"));

        await Triggers.DeleteScheduledAsync("test-operator", trigger.Id);
    }
}

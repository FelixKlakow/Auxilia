using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class TriggerAdministrationTests
{
    private IDataAccess<ScheduledTriggerRecord> _scheduled = null!;
    private IDataAccess<ArtifactTriggerRecord> _artifact = null!;
    private IDataAccess<AuditRecord> _auditRecords = null!;
    private TriggerAdministration _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _scheduled = new InMemoryDataAccess<ScheduledTriggerRecord>();
        _artifact = new InMemoryDataAccess<ArtifactTriggerRecord>();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _sut = new TriggerAdministration(
            _scheduled, _artifact, new AuditLog(_auditRecords, TimeProvider.System));
    }

    [TearDown]
    public void TearDown()
    {
        (_scheduled as IDisposable)?.Dispose();
        (_artifact as IDisposable)?.Dispose();
        (_auditRecords as IDisposable)?.Dispose();
    }

    private static ScheduledTriggerRecord Scheduled(
        int intervalSeconds = 60, string workflowType = "nightly-scan", string? contextJson = null)
        => new()
        {
            WorkflowType = workflowType,
            WorkflowPackageUri = "docker://scan:1",
            IntervalSeconds = intervalSeconds,
            ContextJson = contextJson
        };

    [Test]
    public async Task SaveScheduled_NewTrigger_AssignsIdPersistsAndAudits()
    {
        var saved = await _sut.SaveScheduledAsync("operator-1", Scheduled());

        Assert.Multiple(async () =>
        {
            Assert.That(saved.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(await _scheduled.ReadAsync(saved.Id), Is.Not.Null);
            var audit = (await _auditRecords.ReadAsync()).Single();
            Assert.That(audit.Action, Is.EqualTo("trigger.scheduled.created"));
            Assert.That(audit.Actor, Is.EqualTo("operator-1"));
        });
    }

    [Test]
    public async Task SaveScheduled_ExistingTrigger_KeepsIdAndAuditsUpdate()
    {
        var created = await _sut.SaveScheduledAsync("operator-1", Scheduled());

        var updated = await _sut.SaveScheduledAsync("operator-1", created with { IntervalSeconds = 120 });

        Assert.Multiple(async () =>
        {
            Assert.That(updated.Id, Is.EqualTo(created.Id));
            Assert.That((await _scheduled.ReadAsync(created.Id))!.IntervalSeconds, Is.EqualTo(120));
            var audit = await _auditRecords.ReadAsync();
            Assert.That(audit.Count(a => a.Action == "trigger.scheduled.updated"), Is.EqualTo(1));
        });
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void SaveScheduled_IntervalBelowOneSecond_IsRejected(int intervalSeconds)
        => Assert.That(
            () => _sut.SaveScheduledAsync("operator-1", Scheduled(intervalSeconds)),
            Throws.ArgumentException.With.Message.Contains("Interval"));

    [TestCase("not json")]
    [TestCase("[1,2]")]
    [TestCase("{\"Key\": {\"nested\": true}}")]
    public void SaveScheduled_ContextNotAStringMap_IsRejected(string contextJson)
        => Assert.That(
            () => _sut.SaveScheduledAsync("operator-1", Scheduled(contextJson: contextJson)),
            Throws.ArgumentException.With.Message.Contains("Context"));

    [Test]
    public void SaveScheduled_ValidStringMapContext_IsAccepted()
        => Assert.That(
            () => _sut.SaveScheduledAsync("operator-1", Scheduled(contextJson: "{\"WorkItemId\":\"42\"}")),
            Throws.Nothing);

    [Test]
    public void SaveScheduled_MissingWorkflowType_IsRejected()
        => Assert.That(
            () => _sut.SaveScheduledAsync("operator-1", Scheduled(workflowType: " ")),
            Throws.ArgumentException.With.Message.Contains("Workflow type"));

    [Test]
    public async Task DeleteScheduled_RemovesAndAudits()
    {
        var created = await _sut.SaveScheduledAsync("operator-1", Scheduled());

        var removed = await _sut.DeleteScheduledAsync("operator-1", created.Id);

        Assert.Multiple(async () =>
        {
            Assert.That(removed, Is.True);
            Assert.That(await _scheduled.ReadAsync(created.Id), Is.Null);
            var audit = await _auditRecords.ReadAsync();
            Assert.That(audit.Count(a => a.Action == "trigger.scheduled.deleted"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DeleteScheduled_UnknownId_ReturnsFalseWithoutAudit()
    {
        var removed = await _sut.DeleteScheduledAsync("operator-1", Guid.NewGuid());

        Assert.Multiple(async () =>
        {
            Assert.That(removed, Is.False);
            Assert.That(await _auditRecords.ReadAsync(), Is.Empty);
        });
    }

    private static ArtifactTriggerRecord Artifact(
        string artifactType = "code-review-report", string workflowType = "fix-workflow")
        => new()
        {
            ArtifactType = artifactType,
            WorkflowType = workflowType,
            WorkflowPackageUri = "docker://fix:1"
        };

    [Test]
    public async Task SaveArtifact_UsesDeterministicIdPerTypePair()
    {
        var first = await _sut.SaveArtifactAsync("operator-1", Artifact());
        var second = await _sut.SaveArtifactAsync("operator-1", Artifact() with { Enabled = false });

        Assert.Multiple(async () =>
        {
            Assert.That(second.Id, Is.EqualTo(first.Id));
            var all = await _artifact.ReadAsync();
            Assert.That(all.Count(), Is.EqualTo(1));
            Assert.That(all.Single().Enabled, Is.False);
        });
    }

    [Test]
    public void SaveArtifact_MissingArtifactType_IsRejected()
        => Assert.That(
            () => _sut.SaveArtifactAsync("operator-1", Artifact(artifactType: "")),
            Throws.ArgumentException.With.Message.Contains("Artifact type"));

    [Test]
    public async Task DeleteArtifact_RemovesAndAudits()
    {
        var created = await _sut.SaveArtifactAsync("operator-1", Artifact());

        var removed = await _sut.DeleteArtifactAsync("operator-1", created.Id);

        Assert.Multiple(async () =>
        {
            Assert.That(removed, Is.True);
            var audit = await _auditRecords.ReadAsync();
            Assert.That(audit.Count(a => a.Action == "trigger.artifact.deleted"), Is.EqualTo(1));
        });
    }
}

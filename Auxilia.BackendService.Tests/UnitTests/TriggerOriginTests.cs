using System.Text.Json;
using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData.Entities;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class TriggerOriginTests
{
    private static RunWorkflowCommand Command(
        Dictionary<string, string> context, Guid? configurationId = null)
        => new(Guid.NewGuid(), "code-review", "docker://review:1", context,
            RequestedBy: Guid.NewGuid(), WorkflowConfigurationId: configurationId);

    private static WorkflowInstanceRecord Run(
        RunWorkflowCommand? command, Guid? configurationId = null, string? rawCommandJson = null)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkflowType = "code-review",
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow,
            DispatchCommandJson = rawCommandJson ?? (command is null ? null : JsonSerializer.Serialize(command)),
            WorkflowConfigurationId = configurationId
        };

    [Test]
    public void MailDispatch_SurfacesTheMailSubject()
    {
        var run = Run(Command(new Dictionary<string, string>
        {
            ["WorkItemId"] = "mail-0123456789abcdef",
            ["Title"] = "Please review PR-7",
            ["From"] = "dev@example.com",
            ["Body"] = "…"
        }));

        var origin = TriggerOrigin.Of(run);

        Assert.Multiple(() =>
        {
            Assert.That(origin.Kind, Is.EqualTo(TriggerOriginKind.Mail));
            Assert.That(origin.Label, Is.EqualTo("Mail · Please review PR-7"));
            Assert.That(origin.PredecessorInstanceId, Is.Null);
        });
    }

    [Test]
    public void MailDispatch_WithoutSubject_IsStillRecognisedByTheWorkItemPrefix()
    {
        var run = Run(Command(new Dictionary<string, string> { ["WorkItemId"] = "mail-42" }));

        var origin = TriggerOrigin.Of(run);

        Assert.Multiple(() =>
        {
            Assert.That(origin.Kind, Is.EqualTo(TriggerOriginKind.Mail));
            Assert.That(origin.Label, Is.EqualTo("Mail"));
        });
    }

    [Test]
    public void RerunDispatch_LinksThePredecessor_AndWinsOverTheInheritedMailContext()
    {
        var predecessorId = Guid.NewGuid();
        var run = Run(Command(new Dictionary<string, string>
        {
            ["WorkItemId"] = "mail-42",
            ["Title"] = "Please review PR-7",
            ["RERUN_OF"] = predecessorId.ToString("D")
        }));

        var origin = TriggerOrigin.Of(run);

        Assert.Multiple(() =>
        {
            Assert.That(origin.Kind, Is.EqualTo(TriggerOriginKind.Rerun));
            Assert.That(origin.Label, Is.EqualTo($"Rerun of {predecessorId.ToString("N")[..8]}"));
            Assert.That(origin.PredecessorInstanceId, Is.EqualTo(predecessorId));
        });
    }

    [Test]
    public void ArtifactChainDispatch_IsRecognisedByItsArtifactContext()
    {
        var run = Run(Command(new Dictionary<string, string>
        {
            ["ArtifactId"] = Guid.NewGuid().ToString("D"),
            ["ArtifactType"] = "code-review-report",
            ["WorkItemId"] = "wi-7"
        }, configurationId: Guid.NewGuid()));

        var origin = TriggerOrigin.Of(run);

        Assert.Multiple(() =>
        {
            Assert.That(origin.Kind, Is.EqualTo(TriggerOriginKind.ArtifactChain));
            Assert.That(origin.Label, Is.EqualTo("Artifact chain"));
        });
    }

    [Test]
    public void ConfigurationBoundDispatch_WithoutAdapterContext_IsASchedule()
    {
        var run = Run(Command(new Dictionary<string, string>(), configurationId: Guid.NewGuid()));

        Assert.That(TriggerOrigin.Of(run).Kind, Is.EqualTo(TriggerOriginKind.Schedule));
    }

    [Test]
    public void ConfigurationOnTheRecordOnly_IsAlsoASchedule()
    {
        var run = Run(Command(new Dictionary<string, string>()), configurationId: Guid.NewGuid());

        Assert.That(TriggerOrigin.Of(run).Kind, Is.EqualTo(TriggerOriginKind.Schedule));
    }

    [Test]
    public void ConfigurationLessDispatch_WithoutMarkers_IsManual()
    {
        var run = Run(Command(new Dictionary<string, string> { ["WorkItemId"] = "ticket-1234" }));

        var origin = TriggerOrigin.Of(run);

        Assert.Multiple(() =>
        {
            Assert.That(origin.Kind, Is.EqualTo(TriggerOriginKind.Manual));
            Assert.That(origin.Label, Is.EqualTo("Manual"));
        });
    }

    [Test]
    public void MissingDispatchCommand_IsUnknown()
        => Assert.That(TriggerOrigin.Of(Run(command: null)).Kind, Is.EqualTo(TriggerOriginKind.Unknown));

    [Test]
    public void UnparseableDispatchCommand_IsUnknown()
        => Assert.That(TriggerOrigin.Of(Run(command: null, rawCommandJson: "{not json")).Kind,
            Is.EqualTo(TriggerOriginKind.Unknown));
}

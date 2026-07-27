using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.WorkflowStudio.Triggers;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.WorkflowStudio.Tests.UnitTests;

/// <summary>
/// The artifact-completion trigger keeps its inbound <c>workflow.artifact-events</c> subscription but
/// dispatches the chained workflow through the Core Run API, carrying the artifact reference as run
/// context. Driven by feeding an <see cref="ArtifactPersistedEvent"/> through a fake bus.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class ArtifactTriggerHandlerTests
{
    private static (ArtifactTriggerHandler Handler, FakeMessageBusClient Bus, FakeCoreClient Core,
        InMemoryDataAccess<ArtifactTriggerRecord> Triggers) New()
    {
        var bus = new FakeMessageBusClient();
        var core = new FakeCoreClient();
        var triggers = new InMemoryDataAccess<ArtifactTriggerRecord>();
        var audit = new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System);
        var handler = new ArtifactTriggerHandler(
            bus, core, triggers, audit, NullLogger<ArtifactTriggerHandler>.Instance);
        return (handler, bus, core, triggers);
    }

    private static ArtifactPersistedEvent Event(string artifactType = "design-doc") =>
        new(Guid.NewGuid(), artifactType, "producer-wf", "WI-1", Guid.NewGuid(), 1, "hash", DateTimeOffset.UtcNow);

    [Test]
    public async Task MatchingConfigurationTrigger_DispatchesViaRunConfiguration_WithArtifactContext()
    {
        var (handler, bus, core, triggers) = New();
        var configId = Guid.NewGuid();
        var principal = Guid.NewGuid();
        await triggers.SaveAsync(new ArtifactTriggerRecord
        {
            Id = Guid.NewGuid(),
            ArtifactType = "design-doc",
            WorkflowType = "impl",
            RunAsPrincipalId = principal,
            WorkflowConfigurationId = configId
        }, CancellationToken.None);

        await handler.StartAsync(CancellationToken.None);
        var evt = Event();
        await bus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName, evt, CancellationToken.None);
        await handler.StopAsync(CancellationToken.None);

        Assert.That(core.RunConfigurationIds.Single(), Is.EqualTo(configId));
        Assert.That(core.RunConfigurationOnBehalfOf.Single(), Is.EqualTo(principal));
        var context = core.RunConfigurationContexts.Single()!;
        Assert.That(context["ArtifactId"], Is.EqualTo(evt.ArtifactId.ToString("D")));
        Assert.That(context["ArtifactType"], Is.EqualTo("design-doc"));
        Assert.That(context["WorkItemId"], Is.EqualTo("WI-1"));
    }

    [Test]
    public async Task MatchingInlineTrigger_DispatchesViaRunAsync_WithArtifactContext()
    {
        var (handler, bus, core, triggers) = New();
        var principal = Guid.NewGuid();
        await triggers.SaveAsync(new ArtifactTriggerRecord
        {
            Id = Guid.NewGuid(),
            ArtifactType = "design-doc",
            WorkflowType = "impl",
            RunAsPrincipalId = principal,
            WorkflowConfigurationId = null
        }, CancellationToken.None);

        await handler.StartAsync(CancellationToken.None);
        await bus.SimulateReceivedAsync(ArtifactPersistedEvent.ExchangeName, Event(), CancellationToken.None);
        await handler.StopAsync(CancellationToken.None);

        var request = core.RunRequests.Single();
        Assert.That(request.WorkflowType, Is.EqualTo("impl"));
        Assert.That(request.RequestedBy, Is.EqualTo(principal));
        Assert.That(request.Context!["ArtifactType"], Is.EqualTo("design-doc"));
        Assert.That(core.RunConfigurationIds, Is.Empty);
    }

    [Test]
    public async Task NonMatchingArtifactType_IsIgnored()
    {
        var (handler, bus, core, triggers) = New();
        await triggers.SaveAsync(new ArtifactTriggerRecord
        {
            Id = Guid.NewGuid(),
            ArtifactType = "design-doc",
            WorkflowType = "impl",
            WorkflowConfigurationId = Guid.NewGuid()
        }, CancellationToken.None);

        await handler.StartAsync(CancellationToken.None);
        await bus.SimulateReceivedAsync(
            ArtifactPersistedEvent.ExchangeName, Event("some-other-type"), CancellationToken.None);
        await handler.StopAsync(CancellationToken.None);

        Assert.That(core.RunConfigurationIds, Is.Empty);
        Assert.That(core.RunRequests, Is.Empty);
    }
}

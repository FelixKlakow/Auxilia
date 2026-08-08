using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Unit tests for the run-lifecycle → platform-event translation: terminal states only, the
/// reserved <c>run.*</c> vocabulary, DETERMINISTIC event ids so bus re-delivery collapses
/// to an idempotent upsert instead of duplicate events, and best-effort WorkItemId enrichment
/// from the run's stored dispatch context.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunLifecycleEventPublisherTests
{
    private FakeMessageBusClient _bus = null!;
    private InMemoryDataAccess<CoreRunRecord> _runs = null!;
    private RunLifecycleEventPublisher _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new FakeMessageBusClient();
        _runs = new InMemoryDataAccess<CoreRunRecord>();
        _sut = new RunLifecycleEventPublisher(_bus, _runs, NullLogger<RunLifecycleEventPublisher>.Instance);
        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
        _runs.Dispose();
    }

    private static WorkflowStatusEvent Status(Guid runId, string state)
        => new(runId, "wf-type", state, state == "Failed" ? "boom" : null, DateTimeOffset.UtcNow);

    private IReadOnlyList<WorkflowEventMessage> PublishedEvents()
        => _bus.PublishedMessages
            .Where(p => p.Topic == WorkflowEventMessage.ExchangeName)
            .Select(p => p.Message)
            .OfType<WorkflowEventMessage>()
            .ToList();

    [TestCase("Success", PlatformEventTypes.RunSucceeded)]
    [TestCase("Failed", PlatformEventTypes.RunFailed)]
    [TestCase("PreFlightFailed", PlatformEventTypes.RunFailed)]
    [TestCase("Cancelled", PlatformEventTypes.RunCancelled)]
    public async Task TerminalState_PublishesTheReservedEvent(string state, string expectedType)
    {
        var runId = Guid.NewGuid();
        await _sut.HandleAsync(Status(runId, state), CancellationToken.None);

        var evt = PublishedEvents().Single();
        Assert.Multiple(() =>
        {
            Assert.That(evt.EventType, Is.EqualTo(expectedType));
            Assert.That(evt.WorkflowInstanceId, Is.EqualTo(runId));
            Assert.That(evt.WorkflowType, Is.EqualTo("wf-type"));
            Assert.That(evt.PayloadJson, Does.Contain(state));
        });
    }

    [TestCase("Received")]
    [TestCase("Queued")]
    [TestCase("Running")]
    [TestCase("Draining")]
    public async Task NonTerminalState_PublishesNothing(string state)
    {
        await _sut.HandleAsync(Status(Guid.NewGuid(), state), CancellationToken.None);
        Assert.That(PublishedEvents(), Is.Empty);
    }

    [Test]
    public async Task EventId_IsDeterministicPerRunAndType_AndDistinctAcrossRuns()
    {
        var runId = Guid.NewGuid();
        await _sut.HandleAsync(Status(runId, "Success"), CancellationToken.None);
        await _sut.HandleAsync(Status(runId, "Success"), CancellationToken.None);
        await _sut.HandleAsync(Status(Guid.NewGuid(), "Success"), CancellationToken.None);

        var ids = PublishedEvents().Select(e => e.EventId).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(ids[0], Is.EqualTo(ids[1]),
                "re-delivery of the same terminal transition must mint the SAME id");
            Assert.That(ids[2], Is.Not.EqualTo(ids[0]), "distinct runs mint distinct ids");
        });
    }

    private Task StoreRunAsync(Guid runId, string? commandJson, Guid? commandId = null)
        => _runs.SaveAsync(new CoreRunRecord
        {
            Id = runId,
            WorkflowType = "wf-type",
            State = "Running",
            CommandId = commandId,
            DispatchCommandJson = commandJson,
        }, CancellationToken.None);

    private static string CommandJsonWithContext(IReadOnlyDictionary<string, string> context)
        => System.Text.Json.JsonSerializer.Serialize(new RunWorkflowCommand(
            Guid.NewGuid(), "wf-type", "docker://img", context));

    [Test]
    public async Task WorkItemIdInTheDispatchContext_EnrichesTheEvent()
    {
        var runId = Guid.NewGuid();
        await StoreRunAsync(runId, CommandJsonWithContext(
            new Dictionary<string, string> { ["WorkItemId"] = "WI-4711" }));

        await _sut.HandleAsync(Status(runId, "Success"), CancellationToken.None);

        Assert.That(PublishedEvents().Single().WorkItemId, Is.EqualTo("WI-4711"));
    }

    [Test]
    public async Task RunRecordFoundByCommandId_WhenTheStatusCarriesTheDispatchId()
    {
        // Before the claim rekeys the record, callers (and terminal-sink transitions of
        // never-claimed dispatches) address the run by its command id.
        var commandId = Guid.NewGuid();
        await StoreRunAsync(Guid.NewGuid(), CommandJsonWithContext(
            new Dictionary<string, string> { ["WorkItemId"] = "WI-1" }), commandId);

        await _sut.HandleAsync(Status(commandId, "Failed"), CancellationToken.None);

        Assert.That(PublishedEvents().Single().WorkItemId, Is.EqualTo("WI-1"));
    }

    [Test]
    public async Task NoRunRecord_LeavesWorkItemIdEmpty_AndNeverFails()
    {
        await _sut.HandleAsync(Status(Guid.NewGuid(), "Success"), CancellationToken.None);
        Assert.That(PublishedEvents().Single().WorkItemId, Is.Empty);
    }

    [Test]
    public async Task ContextWithoutWorkItemId_LeavesItEmpty()
    {
        var runId = Guid.NewGuid();
        await StoreRunAsync(runId, CommandJsonWithContext(
            new Dictionary<string, string> { ["Other"] = "x" }));

        await _sut.HandleAsync(Status(runId, "Success"), CancellationToken.None);

        Assert.That(PublishedEvents().Single().WorkItemId, Is.Empty);
    }

    [Test]
    public async Task MalformedDispatchCommandJson_LeavesWorkItemIdEmpty_AndNeverFails()
    {
        var runId = Guid.NewGuid();
        await StoreRunAsync(runId, "not json at all");

        await _sut.HandleAsync(Status(runId, "Cancelled"), CancellationToken.None);

        Assert.That(PublishedEvents().Single().WorkItemId, Is.Empty);
    }

    [Test]
    public async Task Enrichment_NeverChangesTheDeterministicEventId()
    {
        var runId = Guid.NewGuid();
        await _sut.HandleAsync(Status(runId, "Success"), CancellationToken.None);
        await StoreRunAsync(runId, CommandJsonWithContext(
            new Dictionary<string, string> { ["WorkItemId"] = "WI-9" }));
        await _sut.HandleAsync(Status(runId, "Success"), CancellationToken.None);

        var events = PublishedEvents();
        Assert.Multiple(() =>
        {
            Assert.That(events[0].EventId, Is.EqualTo(events[1].EventId),
                "re-delivery may enrich differently but must keep the id — the mirror upsert stays idempotent");
            Assert.That(events[0].WorkItemId, Is.Empty);
            Assert.That(events[1].WorkItemId, Is.EqualTo("WI-9"));
        });
    }

    [Test]
    public async Task BusDelivery_FlowsThroughTheSharedSubscription()
    {
        var runId = Guid.NewGuid();
        await _bus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName, Status(runId, "Cancelled"));

        var evt = PublishedEvents().Single();
        Assert.That(evt.EventType, Is.EqualTo(PlatformEventTypes.RunCancelled));
    }
}

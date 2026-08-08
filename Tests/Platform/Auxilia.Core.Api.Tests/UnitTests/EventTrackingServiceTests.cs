using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Unit tests for the event mirror: bus events land as <see cref="CoreEventRecord"/>s keyed by
/// event id (re-delivery is an idempotent upsert), and the retention sweep drops rows older
/// than <see cref="CoreApiSettings.EventRetentionDays"/>.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EventTrackingServiceTests
{
    private FakeMessageBusClient _bus = null!;
    private IDataAccess<CoreEventRecord> _events = null!;
    private EventTrackingService _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new FakeMessageBusClient();
        _events = new InMemoryDataAccess<CoreEventRecord>();
        _sut = new EventTrackingService(_bus, _events,
            Options.Create(new CoreApiSettings { EventRetentionDays = 30 }),
            NullLogger<EventTrackingService>.Instance);
        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
        _sut.Dispose();
        (_events as IDisposable)?.Dispose();
    }

    private static WorkflowEventMessage Message(
        Guid? eventId = null, string type = "review-ready", DateTimeOffset? at = null)
        => new(eventId ?? Guid.NewGuid(), type, Guid.NewGuid(), "wf", "WI-1", """{"x":1}""",
            at ?? DateTimeOffset.UtcNow);

    [Test]
    public async Task BusEvent_IsMirrored_WithAllFields()
    {
        var msg = Message();
        await _bus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName, msg);

        var record = (await _events.ReadAsync(msg.EventId, CancellationToken.None))!;
        Assert.Multiple(() =>
        {
            Assert.That(record.EventType, Is.EqualTo("review-ready"));
            Assert.That(record.WorkflowType, Is.EqualTo("wf"));
            Assert.That(record.WorkItemId, Is.EqualTo("WI-1"));
            Assert.That(record.SourceRunId, Is.EqualTo(msg.WorkflowInstanceId));
            Assert.That(record.PayloadJson, Is.EqualTo("""{"x":1}"""));
            Assert.That(record.CreatedUtc, Is.EqualTo(msg.TimestampUtc));
        });
    }

    [Test]
    public async Task Redelivery_IsAnIdempotentUpsert()
    {
        var id = Guid.NewGuid();
        await _bus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName, Message(id));
        await _bus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName, Message(id));

        Assert.That((await _events.ReadAsync(CancellationToken.None)).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task SpoofedReservedEvent_IsDropped_ThePlatformsOwnPasses()
    {
        var runId = Guid.NewGuid();
        // A hand-rolled bus client minting run.succeeded with its own id: never mirrored.
        await _bus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            new WorkflowEventMessage(Guid.NewGuid(), "run.succeeded", runId, "wf", "", null,
                DateTimeOffset.UtcNow));
        Assert.That((await _events.ReadAsync(CancellationToken.None)).Count(), Is.EqualTo(0),
            "a reserved-prefix event with a non-platform id must be dropped");

        // The platform's own event carries the deterministic id and lands.
        await _bus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            new WorkflowEventMessage(
                RunLifecycleEventPublisher.DeterministicEventId(runId, "run.succeeded"),
                "run.succeeded", runId, "wf", "", null, DateTimeOffset.UtcNow));
        Assert.That((await _events.ReadAsync(CancellationToken.None)).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task RetentionSweep_DropsOnlyExpiredEvents()
    {
        var fresh = Message(at: DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        var expired = Message(at: DateTimeOffset.UtcNow - TimeSpan.FromDays(31));
        await _bus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName, fresh);
        await _bus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName, expired);

        await _sut.SweepOnceAsync();

        var remaining = (await _events.ReadAsync(CancellationToken.None)).ToList();
        Assert.That(remaining.Select(r => r.Id), Is.EqualTo(new[] { fresh.EventId }));
    }
}

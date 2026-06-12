using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData.Entities;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Live-update path through the real host: a bus message handled by the fan-out hosted
/// services reaches in-process subscribers via the <see cref="LiveViewBroker"/> — the same
/// channel the Blazor circuits consume — with per-view monotonic sequence preserved.
/// </summary>
[TestFixture]
[Category("Component")]
public class LiveUpdatePathTests : DashboardComponentTestBase
{
    private LiveViewBroker Broker => Factory.Services.GetRequiredService<LiveViewBroker>();

    [OneTimeSetUp]
    public void StartHost()
    {
        // Resolving a client boots the host so the fan-out hosted services subscribe.
        using var _ = CreateClient();
    }

    [Test]
    public async Task ViewDataMessage_FromBus_ReachesInProcessSubscriber()
    {
        var received = new List<ViewDataMessage>();
        using var subscription = Broker.Subscribe(onViewData: received.Add);
        var message = new ViewDataMessage(Guid.NewGuid(), "review-log", 1, """{"line":"live"}""");

        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName, message);

        Assert.That(await MessageBus.WaitForConditionAsync(
            () => received.Contains(message), TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public async Task StatusEvent_FromBus_ReachesInProcessSubscriber()
    {
        var received = new List<WorkflowStatusEvent>();
        using var subscription = Broker.Subscribe(onStatus: received.Add);
        var statusEvent = new WorkflowStatusEvent(
            Guid.NewGuid(), "code-review", "Running", null, DateTimeOffset.UtcNow);

        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName, statusEvent);

        Assert.That(await MessageBus.WaitForConditionAsync(
            () => received.Contains(statusEvent), TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public async Task LiveItems_WithDuplicateAndOutOfOrderSequences_RenderMonotonically()
    {
        var instanceId = Guid.NewGuid();
        var buffer = new ViewItemBuffer();
        using var subscription = Broker.Subscribe(onViewData: message =>
            buffer.Add(new ViewDataRecord
            {
                Id = ViewDataRecord.IdFor(message.WorkflowInstanceId, message.ViewName, message.Sequence),
                WorkflowInstanceId = message.WorkflowInstanceId,
                ViewName = message.ViewName,
                Sequence = message.Sequence,
                PayloadJson = message.PayloadJson,
                TimestampUtc = DateTimeOffset.UtcNow
            }));

        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "log", 2, """{"n":2}"""));
        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "log", 1, """{"n":1}"""));
        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "log", 2, """{"n":"duplicate"}"""));

        Assert.That(await MessageBus.WaitForConditionAsync(
            () => buffer.Items.Count == 2, TimeSpan.FromSeconds(5)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(buffer.Items.Select(i => i.Sequence), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(buffer.Items.Single(i => i.Sequence == 2).PayloadJson, Does.Contain("\"n\":2"));
        });
    }
}

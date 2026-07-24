using Auxilia.Core.Runner.Tests.ComponentTests;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class LongLivingDrainCoordinatorTests
{
    private FakeMessageBusClient _bus = null!;
    private WorkflowInstanceRegistry _registry = null!;
    private LongLivingDrainCoordinator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new FakeMessageBusClient();
        _registry = TestStores.NewWorkflowInstanceRegistry();
        _sut = TestStores.NewDrainCoordinator(_bus, _registry);
    }

    [Test]
    public async Task DrainRunningInstancesAsync_RunningLongLivingInstance_PublishesDrainCommandToPerInstanceQueue()
    {
        var instanceId = Guid.NewGuid();
        await _registry.RegisterAsync(instanceId, "service-workflow", "LongLiving");

        await _sut.DrainRunningInstancesAsync("service-workflow");

        var drainMessages = _bus.PublishedMessages
            .Where(m => m.Topic == $"workflow-drain-{instanceId}").ToList();
        Assert.That(drainMessages, Has.Count.EqualTo(1));
        var command = (DrainWorkflowCommand)drainMessages[0].Message;
        Assert.That(command.WorkflowInstanceId, Is.EqualTo(instanceId));
    }

    [Test]
    public async Task DrainRunningInstancesAsync_RunningLongLivingInstance_SetsRecordStateToDraining()
    {
        var instanceId = Guid.NewGuid();
        await _registry.RegisterAsync(instanceId, "service-workflow", "LongLiving");

        await _sut.DrainRunningInstancesAsync("service-workflow");

        var record = await _registry.GetAsync(instanceId);
        Assert.That(record, Is.Not.Null);
        Assert.That(record!.State, Is.EqualTo("Draining"));
    }

    [Test]
    public async Task DrainRunningInstancesAsync_RunningLongLivingInstance_PublishesDrainingStatusEvent()
    {
        var instanceId = Guid.NewGuid();
        await _registry.RegisterAsync(instanceId, "service-workflow", "LongLiving");

        await _sut.DrainRunningInstancesAsync("service-workflow");

        var statusEvents = _bus.PublishedMessages
            .Where(m => m.Topic == WorkflowStatusEvent.ExchangeName)
            .Select(m => m.Message)
            .OfType<WorkflowStatusEvent>()
            .ToList();
        Assert.That(statusEvents, Has.Count.EqualTo(1));
        Assert.That(statusEvents[0].WorkflowInstanceId, Is.EqualTo(instanceId));
        Assert.That(statusEvents[0].WorkflowType, Is.EqualTo("service-workflow"));
        Assert.That(statusEvents[0].State, Is.EqualTo("Draining"));
    }

    [Test]
    public async Task DrainRunningInstancesAsync_RunningOneShotInstanceOfSameType_IsUntouched()
    {
        var longLivingId = Guid.NewGuid();
        var oneShotId = Guid.NewGuid();
        await _registry.RegisterAsync(longLivingId, "service-workflow", "LongLiving");
        await _registry.RegisterAsync(oneShotId, "service-workflow");

        await _sut.DrainRunningInstancesAsync("service-workflow");

        var oneShotRecord = await _registry.GetAsync(oneShotId);
        Assert.That(oneShotRecord!.State, Is.EqualTo("Running"));
        Assert.That(
            _bus.PublishedMessages.Any(m => m.Topic == $"workflow-drain-{oneShotId}"),
            Is.False, "One-shot instances must not receive a drain command.");
    }
}

using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Tests.ComponentTests;
using Auxilia.Core.Runner.Workflows;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class ViewDataHandlerTests
{
    private const long MaxItemsPerView = 3;

    private FakeMessageBusClient _fakeBus = null!;
    private InMemoryDataAccess<ViewDataRecord> _viewData = null!;
    private WorkflowInstanceRegistry _registry = null!;
    private ViewDataHandler _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fakeBus = new FakeMessageBusClient();
        _viewData = new InMemoryDataAccess<ViewDataRecord>();
        _registry = TestStores.NewWorkflowInstanceRegistry();
        _sut = new ViewDataHandler(
            _fakeBus,
            _viewData,
            _registry,
            TimeProvider.System,
            Options.Create(new WorkflowDispatcherSettings { MaxViewItemsPerView = MaxItemsPerView }),
            NullLogger<ViewDataHandler>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync();
        _viewData.Dispose();
    }

    private async Task<Guid> RegisterInstanceWithViewAsync(ViewLifecycle lifecycle, string viewName = "progress")
    {
        var instanceId = Guid.NewGuid();
        var viewsJson = JsonSerializer.Serialize(new List<ViewDescriptor>
        {
            new(viewName, "{}", ViewRendering.Stream, lifecycle)
        });
        await _registry.RegisterAsync(instanceId, "TestWorkflow", viewsJson: viewsJson);
        return instanceId;
    }

    private async Task<List<ViewDataRecord>> SavedRecordsAsync()
        => (await _viewData.ReadAsync()).ToList();

    [Test]
    public async Task WhenItemForPersistedViewReceived_SavesRecordWithDeterministicIdAndPayload()
    {
        var instanceId = await RegisterInstanceWithViewAsync(ViewLifecycle.Persisted);

        await _fakeBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "progress", 1, """{"step":"clone"}"""));

        var saved = await SavedRecordsAsync();
        Assert.That(saved, Has.Count.EqualTo(1));
        Assert.That(saved[0].Id, Is.EqualTo(ViewDataRecord.IdFor(instanceId, "progress", 1)));
        Assert.That(saved[0].WorkflowInstanceId, Is.EqualTo(instanceId));
        Assert.That(saved[0].ViewName, Is.EqualTo("progress"));
        Assert.That(saved[0].Sequence, Is.EqualTo(1));
        Assert.That(saved[0].PayloadJson, Is.EqualTo("""{"step":"clone"}"""));
        Assert.That(saved[0].TimestampUtc, Is.Not.EqualTo(default(DateTimeOffset)));
    }

    [Test]
    public async Task WhenItemForLiveAndPersistedViewReceived_SavesRecord()
    {
        var instanceId = await RegisterInstanceWithViewAsync(ViewLifecycle.LiveAndPersisted);

        await _fakeBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "progress", 1, "{}"));

        Assert.That(await SavedRecordsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task WhenItemForLiveOnlyViewReceived_DoesNotSave()
    {
        var instanceId = await RegisterInstanceWithViewAsync(ViewLifecycle.Live);

        await _fakeBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "progress", 1, "{}"));

        Assert.That(await SavedRecordsAsync(), Is.Empty);
    }

    [Test]
    public async Task WhenItemForUndeclaredViewReceived_DoesNotSave()
    {
        var instanceId = await RegisterInstanceWithViewAsync(ViewLifecycle.Persisted);

        await _fakeBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "not-declared", 1, "{}"));

        Assert.That(await SavedRecordsAsync(), Is.Empty);
    }

    [Test]
    public async Task WhenItemForUnknownInstanceReceived_DoesNotSave()
    {
        await _fakeBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(Guid.NewGuid(), "progress", 1, "{}"));

        Assert.That(await SavedRecordsAsync(), Is.Empty);
    }

    [Test]
    public async Task WhenSequenceExceedsPerViewCap_DoesNotSave()
    {
        var instanceId = await RegisterInstanceWithViewAsync(ViewLifecycle.Persisted);

        await _fakeBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "progress", MaxItemsPerView + 1, "{}"));

        Assert.That(await SavedRecordsAsync(), Is.Empty);
    }

    [Test]
    public async Task WhenSequenceEqualsPerViewCap_SavesRecord()
    {
        var instanceId = await RegisterInstanceWithViewAsync(ViewLifecycle.Persisted);

        await _fakeBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "progress", MaxItemsPerView, "{}"));

        Assert.That(await SavedRecordsAsync(), Has.Count.EqualTo(1));
    }
}

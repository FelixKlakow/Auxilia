using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Auxilia.SteeringInstance.Tests.ComponentTests;

[TestFixture]
[Category("Component")]
public class WorkflowCancelPipelineComponentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private IHost _host = null!;
    private FakeMessageBusClient _bus = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new FakeMessageBusClient();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMessageBusClient>(_bus);

                var platformData = new PlatformDataSettings { Backend = PlatformDataBackend.InMemory };
                services.AddPlatformEntity<WorkflowInstanceRecord>(platformData);
                services.AddPlatformEntity<AuditRecord>(platformData);
                services.AddSingleton<AuditLog>();

                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<WorkflowInstanceRegistry>();
                services.AddSingleton<WorkflowStateHandler>();
                services.AddSingleton<WorkflowCancelDispatcher>();
            })
            .Build();

        await _host.Services.GetRequiredService<WorkflowStateHandler>().StartAsync(CancellationToken.None);
        await _host.Services.GetRequiredService<WorkflowCancelDispatcher>().StartAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown()
    {
        _host.Dispose();
    }

    [Test]
    public async Task WhenCancelCommandPublished_KnownInstance_CancelCommandPublishedToPerInstanceTopic()
    {
        var instanceId = Guid.NewGuid();
        var registry = _host.Services.GetRequiredService<WorkflowInstanceRegistry>();
        await registry.RegisterAsync(instanceId, "test-workflow");

        var command = new CancelWorkflowCommand(instanceId);
        var expectedTopic = $"workflow-cancel-{instanceId}";

        await _bus.SimulateReceivedAsync("workflow.cancel-commands", command);

        var published = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == expectedTopic),
            Timeout);

        Assert.That(published, Is.True, $"No message published to {expectedTopic} within timeout.");

        var (_, msg) = _bus.PublishedMessages.First(m => m.Topic == expectedTopic);
        var cancelCommand = msg as CancelWorkflowCommand;
        Assert.That(cancelCommand, Is.Not.Null);
        Assert.That(cancelCommand!.WorkflowInstanceId, Is.EqualTo(instanceId));
    }

    [Test]
    public async Task WhenCancelCommandPublished_UnknownInstance_NothingPublished()
    {
        var unknownId = Guid.NewGuid();
        var command = new CancelWorkflowCommand(unknownId);

        await _bus.SimulateReceivedAsync("workflow.cancel-commands", command);

        await Task.Delay(200);

        var cancelTopic = $"workflow-cancel-{unknownId}";
        Assert.That(
            _bus.PublishedMessages.Any(m => m.Topic == cancelTopic),
            Is.False,
            "A cancel message was unexpectedly published for an unknown instance.");
    }

    [Test]
    public async Task WhenCancelledStateArrives_DoesNotThrow()
    {
        var message = new WorkflowStateMessage(Guid.NewGuid(), WorkflowState.Cancelled, null);

        Assert.DoesNotThrowAsync(
            () => _bus.SimulateReceivedAsync("workflow.state", message));

        await Task.CompletedTask;
    }
}

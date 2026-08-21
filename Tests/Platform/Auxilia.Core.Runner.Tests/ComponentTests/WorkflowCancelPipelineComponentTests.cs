using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Artifacts;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Auxilia.Core.Runner.Tests.ComponentTests;

[TestFixture]
[Category("Component")]
public class WorkflowCancelPipelineComponentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private IHost _host = null!;
    private FakeMessageBusClient _bus = null!;
    private string _tempDir = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new FakeMessageBusClient();
        _tempDir = Path.Combine(Path.GetTempPath(), $"auxilia-cancel-pipeline-{Guid.NewGuid():N}");

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMessageBusClient>(_bus);

                var platformData = new PlatformDataSettings
                {
                    Backend = PlatformDataBackend.InMemory,
                    JsonDirectory = _tempDir
                };
                services.AddPlatformEntity<WorkflowInstanceRecord>(platformData);
                services.AddPlatformEntity<AuditRecord>(platformData);
                services.AddPlatformEntity<ArtifactRecord>(platformData);
                services.AddSingleton(platformData);
                services.AddSingleton<AuditLog>();

                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<WorkflowStatusPublisher>();
                services.AddSingleton<WorkflowInstanceRegistry>();
                services.AddSingleton<Auxilia.Core.Runner.Workflows.Storage.WorkflowInstanceTokenRegistry>();
                services.AddSingleton(new ArtifactStoreSettings());
                services.AddSingleton<IArtifactStore, FileSystemArtifactStore>();
                services.AddSingleton<ArtifactPersister>();
                services.AddSingleton<WorkspaceManager>();
                services.AddSingleton<Auxilia.Core.Runner.Workflows.Pods.IPodHost>(new Auxilia.Core.Runner.Tests.Workflows.FakePodHost());
                services.AddSingleton<Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry>();
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
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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

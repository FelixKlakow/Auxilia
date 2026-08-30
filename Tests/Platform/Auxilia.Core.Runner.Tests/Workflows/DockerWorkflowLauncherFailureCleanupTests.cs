using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Pods;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// A failed launch must leave nothing behind: pod materialization failures tear the partial
/// pod down, and any failure between container creation and start removes the created
/// container. Mock <see cref="IDockerClientFactory"/> — no live Docker daemon required.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DockerWorkflowLauncherFailureCleanupTests
{
    private static readonly DockerWorkflowLauncherSettings DefaultSettings = new()
    {
        DockerSocketPath = "unix:///var/run/docker.sock",
        RuntimeImage = "mcr.microsoft.com/dotnet/runtime:8.0"
    };

    private Mock<IContainerOperations> _mockContainers = null!;
    private Mock<INetworkOperations> _mockNetworks = null!;
    private Mock<IDockerClient> _mockClient = null!;
    private Mock<IDockerClientFactory> _mockFactory = null!;
    private FakePodHost _podHost = null!;
    private DockerWorkflowLauncher _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _mockContainers = new Mock<IContainerOperations>();
        _mockContainers
            .Setup(c => c.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateContainerResponse { ID = "abc123" });
        _mockContainers
            .Setup(c => c.StartContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        // The exit watcher (spawned after a successful start) must idle forever — otherwise
        // its own container removal races the assertions below.
        _mockContainers
            .Setup(c => c.WaitContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<ContainerWaitResponse>().Task);

        _mockNetworks = new Mock<INetworkOperations>();

        _mockClient = new Mock<IDockerClient>();
        _mockClient.Setup(c => c.Containers).Returns(_mockContainers.Object);
        _mockClient.Setup(c => c.Networks).Returns(_mockNetworks.Object);

        _mockFactory = new Mock<IDockerClientFactory>();
        _mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(_mockClient.Object);

        _podHost = new FakePodHost();
        _sut = new DockerWorkflowLauncher(
            Options.Create(DefaultSettings),
            _mockFactory.Object,
            _podHost,
            NullLogger<DockerWorkflowLauncher>.Instance);
    }

    private static WorkflowLaunchRequest BakedImageRequest(PodPlan? pod = null)
        => new(string.Empty, new Dictionary<string, string>(), [])
        {
            DockerImageUri = "example/workflow:1",
            Pod = pod
        };

    private static PodPlan PodPlanFor(Guid instanceId)
        => new(instanceId, $"auxilia-pod-{instanceId:N}", [], [], new Dictionary<string, string>());

    // ------------------------------------------------------------------ Pod materialization

    [Test]
    public void WhenPodMaterializationFails_PodIsTornDownAndTheFailurePropagates()
    {
        var instanceId = Guid.NewGuid();
        _podHost.MaterializeFailure = new InvalidOperationException(
            "companion 'db-2' did not become ready within 30s");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.LaunchAsync(BakedImageRequest(PodPlanFor(instanceId)), CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("did not become ready"));
        Assert.That(_podHost.TornDown, Does.Contain(instanceId),
            "a companion failing readiness mid-pod must not leave the earlier companions, "
            + "network, or volumes running until the next runner restart");
        _mockContainers.Verify(c => c.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()),
            Times.Never, "the workflow container is never created when its pod failed");
    }

    // ------------------------------------------------------------------ Post-create pre-start

    [Test]
    public void WhenConnectingThePodNetworkFails_CreatedContainerIsRemoved()
    {
        var instanceId = Guid.NewGuid();
        _mockNetworks
            .Setup(n => n.ConnectNetworkAsync(
                It.IsAny<string>(), It.IsAny<NetworkConnectParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network gone"));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.LaunchAsync(BakedImageRequest(PodPlanFor(instanceId)), CancellationToken.None));

        _mockContainers.Verify(c => c.RemoveContainerAsync(
                "abc123", It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "a created-but-unstarted container must not linger (with pod volumes bound) until restart");
        Assert.That(_podHost.TornDown, Does.Contain(instanceId), "the pod dies with the failed launch");
        _mockContainers.Verify(c => c.StartContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public void WhenPluginArchiveInjectionFails_CreatedContainerIsRemoved()
    {
        // A baked-image launch with a plugin whose DLL is not a readable assembly skips the
        // compatibility check and goes straight to the tar injection — which fails here.
        var pluginDir = Path.Combine(Path.GetTempPath(), $"auxilia-fail-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginDir);
        try
        {
            var dllPath = Path.Combine(pluginDir, "stub.slothandler.dll");
            var manifestPath = Path.Combine(pluginDir, "stub.slothandler.manifest.json");
            File.WriteAllBytes(dllPath, [0xDE, 0xAD]);
            File.WriteAllText(manifestPath, "{}");
            _mockContainers
                .Setup(c => c.ExtractArchiveToContainerAsync(
                    It.IsAny<string>(), It.IsAny<ContainerPathStatParameters>(),
                    It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("archive write failed"));
            var request = new WorkflowLaunchRequest(
                string.Empty, new Dictionary<string, string>(),
                [new SlotPluginFile(dllPath, manifestPath)])
            {
                DockerImageUri = "example/workflow:1"
            };

            Assert.ThrowsAsync<IOException>(() => _sut.LaunchAsync(request, CancellationToken.None));

            _mockContainers.Verify(c => c.RemoveContainerAsync(
                    "abc123", It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()),
                Times.Once);
            _mockContainers.Verify(c => c.StartContainerAsync(
                    It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(pluginDir, recursive: true);
        }
    }

    [Test]
    public void WhenStartContainerFails_CreatedContainerIsRemoved()
    {
        _mockContainers
            .Setup(c => c.StartContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("start refused"));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.LaunchAsync(BakedImageRequest(), CancellationToken.None));

        _mockContainers.Verify(c => c.RemoveContainerAsync(
                "abc123", It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenLaunchSucceeds_NothingIsRemoved()
    {
        await _sut.LaunchAsync(BakedImageRequest(), CancellationToken.None);

        _mockContainers.Verify(c => c.RemoveContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockContainers.Verify(c => c.StartContainerAsync(
                "abc123", It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

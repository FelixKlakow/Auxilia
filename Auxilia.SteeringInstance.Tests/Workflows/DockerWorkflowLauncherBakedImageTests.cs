using Auxilia.SteeringInstance.Workflows;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.SteeringInstance.Tests.Workflows;

/// <summary>
/// Unit tests for <see cref="DockerWorkflowLauncher"/> baked-image container parameters
/// and slot-plugin injection via Docker tar API.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DockerWorkflowLauncherBakedImageTests
{
    private static readonly DockerWorkflowLauncherSettings DefaultSettings = new()
    {
        DockerSocketPath = "unix:///var/run/docker.sock",
        RuntimeImage     = "mcr.microsoft.com/dotnet/runtime:8.0",
        NetworkName      = "auxilia-net"
    };

    private static WorkflowLaunchRequest MakeBakedRequest(
        string imageUri = "my-image:tag",
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyList<SlotPluginFile>? pluginFiles = null,
        string? pluginDirectory = null)
        => new WorkflowLaunchRequest(
            string.Empty,
            env ?? new Dictionary<string, string>(),
            pluginFiles ?? [],
            DockerImageUri: imageUri,
            PluginDirectory: pluginDirectory);

    // -----------------------------------------------------------------------
    // BuildBakedImageContainerParameters tests

    [Test]
    public void BuildBakedImageContainerParameters_SetsImageFromDockerImageUri()
    {
        var request = MakeBakedRequest("my-image:tag");
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(request, DefaultSettings);

        Assert.That(p.Image, Is.EqualTo("my-image:tag"));
    }

    [Test]
    public void BuildBakedImageContainerParameters_NoWorkflowBindMount()
    {
        var request = MakeBakedRequest();
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(request, DefaultSettings);

        var binds = p.HostConfig?.Binds;
        var hasWorkflowMount = binds != null && binds.Any(b => b.Contains("/workflow"));
        Assert.That(hasWorkflowMount, Is.False, "Baked-image containers must not have a /workflow bind-mount.");
    }

    [Test]
    public void BuildBakedImageContainerParameters_CmdIsNullOrEmpty()
    {
        var request = MakeBakedRequest();
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(request, DefaultSettings);

        Assert.That(p.Cmd == null || p.Cmd.Count == 0, Is.True);
    }

    [Test]
    public void BuildBakedImageContainerParameters_NetworkIsAttached_WhenNetworkNameSet()
    {
        var request = MakeBakedRequest();
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(request, DefaultSettings);

        Assert.That(p.NetworkingConfig, Is.Not.Null);
        Assert.That(p.NetworkingConfig!.EndpointsConfig.ContainsKey("auxilia-net"), Is.True);
    }

    [Test]
    public void BuildBakedImageContainerParameters_DefaultDenyWithNoEndpoints_AttachesInternalNetwork()
    {
        var settings = new DockerWorkflowLauncherSettings
        {
            NetworkName = "auxilia-net",
            InternalNetworkName = "auxilia-internal"
        };
        var request = MakeBakedRequest() with
        {
            NetworkPolicy = new EffectiveNetworkPolicy(NetworkPolicyMode.DefaultDeny, [])
        };

        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(request, settings);

        Assert.That(p.NetworkingConfig!.EndpointsConfig.Keys, Is.EqualTo(new[] { "auxilia-internal" }));
    }

    [Test]
    public async Task LaunchAsync_WhenDockerImageUri_CallsBakedImagePath_NotExtractedPath()
    {
        // Arrange — mock Docker client
        ContainerPathStatParameters? capturedPathParams = null;

        var mockContainers = new Mock<IContainerOperations>();
        mockContainers
            .Setup(c => c.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateContainerResponse { ID = "abc123" });
        mockContainers
            .Setup(c => c.StartContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockContainers
            .Setup(c => c.ExtractArchiveToContainerAsync(
                It.IsAny<string>(),
                It.IsAny<ContainerPathStatParameters>(),
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, ContainerPathStatParameters, Stream, CancellationToken>(
                (_, pathParams, _, _) => capturedPathParams = pathParams)
            .Returns(Task.CompletedTask);

        var mockClient = new Mock<IDockerClient>();
        mockClient.Setup(c => c.Containers).Returns(mockContainers.Object);

        var mockFactory = new Mock<IDockerClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(mockClient.Object);

        var sut = new DockerWorkflowLauncher(
            Options.Create(DefaultSettings),
            mockFactory.Object,
            NullLogger<DockerWorkflowLauncher>.Instance);

        // Create a stub plugin file in a temp dir
        var tempDir = Path.Combine(Path.GetTempPath(), $"baked-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath      = Path.Combine(tempDir, "stub.slothandler.dll");
            var manifestPath = Path.Combine(tempDir, "stub.slothandler.manifest.json");
            File.WriteAllBytes(dllPath, [0xDE, 0xAD, 0xBE, 0xEF]);
            File.WriteAllText(manifestPath, "{}");
            var pluginFile = new SlotPluginFile(dllPath, manifestPath);

            var request = MakeBakedRequest(
                imageUri: "my-image:tag",
                pluginFiles: [pluginFile]);

            // Act
            await sut.LaunchAsync(request, CancellationToken.None);

            // Assert: container created with baked-image params (no /workflow bind)
            mockContainers.Verify(c => c.CreateContainerAsync(
                It.Is<CreateContainerParameters>(p =>
                    p.Image == "my-image:tag" &&
                    (p.HostConfig.Binds == null || !p.HostConfig.Binds.Any(b => b.Contains("/workflow")))),
                It.IsAny<CancellationToken>()),
                Times.Once);

            // Assert: tar injection was called exactly once with the default plugin dir
            mockContainers.Verify(c => c.ExtractArchiveToContainerAsync(
                It.IsAny<string>(),
                It.IsAny<ContainerPathStatParameters>(),
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()),
                Times.Once);

            Assert.That(capturedPathParams, Is.Not.Null);
            Assert.That(capturedPathParams!.Path, Is.EqualTo("/app"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

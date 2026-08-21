using Auxilia.Core.Runner.Workflows;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

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
    public void BuildBakedImageContainerParameters_WorkspaceDirectoryBindSet_AddsReadWriteWorkspaceMount()
    {
        var request = MakeBakedRequest() with { WorkspaceDirectoryBind = "/host/workspaces/run1" };
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(request, DefaultSettings);

        Assert.That(p.HostConfig.Binds, Does.Contain("/host/workspaces/run1:/workspace"),
            "The workspace must be mounted read-write — workflows commit locally; pushes go through slots.");
    }

    [Test]
    public void BuildBakedImageContainerParameters_NoWorkspaceDirectoryBind_NoWorkspaceMount()
    {
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(MakeBakedRequest(), DefaultSettings);

        Assert.That(p.HostConfig.Binds.Any(b => b.EndsWith(":/workspace")), Is.False);
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
            new FakePodHost(),
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

    [Test]
    public void BuildBakedImageContainerParameters_WithTerminal_PublishesAnEphemeralLoopbackPort()
    {
        var request = MakeBakedRequest() with
        {
            PublishTerminalPort = 7681,
            TerminalContainerName = "auxilia-session-abc"
        };

        // Default mode "loopback": a host-process Core reaches the terminal via 127.0.0.1.
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(request, DefaultSettings);

        Assert.Multiple(() =>
        {
            Assert.That(p.ExposedPorts, Contains.Key("7681/tcp"));
            Assert.That(p.Name, Is.EqualTo("auxilia-session-abc"));
            var binding = p.HostConfig.PortBindings["7681/tcp"].Single();
            Assert.That(binding.HostIP, Is.EqualTo("127.0.0.1"),
                "the terminal must never listen on a non-local interface");
            Assert.That(binding.HostPort, Is.Empty, "the daemon assigns an ephemeral port");
        });
    }

    [Test]
    public void BuildBakedImageContainerParameters_ContainerNetworkMode_PublishesNoHostPort()
    {
        var request = MakeBakedRequest() with
        {
            PublishTerminalPort = 7681,
            TerminalContainerName = "auxilia-session-abc"
        };
        var settings = new DockerWorkflowLauncherSettings { TerminalPublishMode = "container-network" };

        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(request, settings);

        Assert.Multiple(() =>
        {
            Assert.That(p.ExposedPorts, Contains.Key("7681/tcp"));
            Assert.That(p.Name, Is.EqualTo("auxilia-session-abc"),
                "the deterministic name is how a containerized Core reaches the terminal on the shared network");
            Assert.That(p.HostConfig.PortBindings, Is.Null, "no host port — proxy is container-to-container");
        });
    }

    [Test]
    public void BuildBakedImageContainerParameters_WithoutTerminalPort_ExposesNothing()
    {
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(MakeBakedRequest(), DefaultSettings);

        Assert.That(p.ExposedPorts, Is.Null);
    }

    [Test]
    public void ComposedImageTag_IsDeterministic_AndContentAddressed()
    {
        var same1 = DockerWorkflowLauncher.ComposedImageTag("img:1", ["RUN a", "RUN b"]);
        var same2 = DockerWorkflowLauncher.ComposedImageTag("img:1", ["RUN a", "RUN b"]);
        var otherLayers = DockerWorkflowLauncher.ComposedImageTag("img:1", ["RUN a"]);
        var otherBase = DockerWorkflowLauncher.ComposedImageTag("img:2", ["RUN a", "RUN b"]);

        Assert.Multiple(() =>
        {
            Assert.That(same1, Is.EqualTo(same2), "same base + layers must reuse the cached image");
            Assert.That(same1, Does.StartWith("auxilia-env:"));
            Assert.That(same1, Is.Not.EqualTo(otherLayers).And.Not.EqualTo(otherBase),
                "any change to the base or a fragment must produce a fresh image");
        });
    }

    [Test]
    public void ComposeDockerfile_LayersFragmentsOnTopOfTheWorkflowImage()
    {
        var dockerfile = DockerWorkflowLauncher.ComposeDockerfile(
            "auxilia-claude-code-workflow:system-test",
            ["# dotnet\nRUN install-dotnet", "RUN install-node"]);

        Assert.That(dockerfile, Is.EqualTo(
            "FROM auxilia-claude-code-workflow:system-test\n# dotnet\nRUN install-dotnet\nRUN install-node\n"));
    }
}

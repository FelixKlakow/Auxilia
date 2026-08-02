using System.Text.Json;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Workflows.Crypto;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Unit tests for <see cref="DockerWorkflowLauncher"/> host-filesystem copy-in behavior.
/// Uses a mock <see cref="IDockerClientFactory"/> — no live Docker daemon required.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DockerWorkflowLauncherCopyInTests
{
    private string _extractedContentDir = null!;
    private string _pluginSourceDir = null!;
    private Mock<IDockerClientFactory> _mockFactory = null!;
    private Mock<IDockerClient> _mockClient = null!;
    private DockerWorkflowLauncher _sut = null!;

    private static readonly DockerWorkflowLauncherSettings DefaultSettings = new()
    {
        DockerSocketPath = "unix:///var/run/docker.sock",
        RuntimeImage = "mcr.microsoft.com/dotnet/runtime:8.0"
    };

    [SetUp]
    public void SetUp()
    {
        _extractedContentDir = Path.Combine(Path.GetTempPath(), $"auxilia-copyin-test-{Guid.NewGuid()}");
        _pluginSourceDir = Path.Combine(Path.GetTempPath(), $"auxilia-plugins-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_extractedContentDir);
        Directory.CreateDirectory(_pluginSourceDir);

        WriteManifest(_extractedContentDir, "bin/my-workflow");

        var mockContainers = new Mock<IContainerOperations>();
        mockContainers
            .Setup(c => c.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateContainerResponse { ID = "abc123" });
        mockContainers
            .Setup(c => c.StartContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockClient = new Mock<IDockerClient>();
        _mockClient.Setup(c => c.Containers).Returns(mockContainers.Object);

        _mockFactory = new Mock<IDockerClientFactory>();
        _mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(_mockClient.Object);

        _sut = new DockerWorkflowLauncher(
            Options.Create(DefaultSettings),
            _mockFactory.Object,
            NullLogger<DockerWorkflowLauncher>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_extractedContentDir))
            Directory.Delete(_extractedContentDir, recursive: true);
        if (Directory.Exists(_pluginSourceDir))
            Directory.Delete(_pluginSourceDir, recursive: true);
    }

    private static void WriteManifest(string dir, string executableRelativePath)
    {
        var manifest = new WorkflowPackageManifest(
            Files: [],
            SignatureBase64: "c2ln",
            PublicKeyBase64: "a2V5",
            ExecutableRelativePath: executableRelativePath);
        var json = JsonSerializer.Serialize(manifest, WorkflowPackageJsonOptions.SerializeOptions);
        File.WriteAllText(Path.Combine(dir, "package-manifest.json"), json);
    }

    private SlotPluginFile WriteStubPlugin(string dllName)
    {
        var dllPath = Path.Combine(_pluginSourceDir, dllName);
        var manifestPath = Path.Combine(_pluginSourceDir, Path.GetFileNameWithoutExtension(dllName) + ".manifest.json");
        File.WriteAllBytes(dllPath, [0xDE, 0xAD, 0xBE, 0xEF]);
        File.WriteAllText(manifestPath, "{}");
        return new SlotPluginFile(dllPath, manifestPath);
    }

    private WorkflowLaunchRequest MakeRequest(IReadOnlyList<SlotPluginFile> pluginFiles)
        => new(_extractedContentDir, new Dictionary<string, string>(), pluginFiles);

    // ------------------------------------------------------------------ tests

    [Test]
    public async Task WhenSlotPluginFilesEmpty_NoFilesAreCopiedToExtractedContentDirectory()
    {
        await _sut.LaunchAsync(MakeRequest([]), CancellationToken.None);

        var filesInDir = Directory.GetFiles(_extractedContentDir, "*", SearchOption.AllDirectories);
        Assert.That(filesInDir, Has.Length.EqualTo(1), "Only package-manifest.json should be present.");
        Assert.That(filesInDir[0], Does.EndWith("package-manifest.json"));
    }

    [Test]
    public async Task WhenSlotPluginFilesHasOneEntry_DllAndManifestAreCopiedToExpectedSubdirectory()
    {
        var plugin = WriteStubPlugin("my-workflow.slothandler.dll");
        await _sut.LaunchAsync(MakeRequest([plugin]), CancellationToken.None);

        var expectedDll = Path.Combine(_extractedContentDir, "bin", "my-workflow.slothandler.dll");
        var expectedManifest = Path.Combine(_extractedContentDir, "bin", "my-workflow.slothandler.manifest.json");
        Assert.That(File.Exists(expectedDll), Is.True, $"Expected DLL at {expectedDll}");
        Assert.That(File.Exists(expectedManifest), Is.True, $"Expected manifest at {expectedManifest}");
    }

    [Test]
    public async Task WhenSlotPluginFilesHasTwoEntries_AllFourFilesAreCopied()
    {
        var plugin1 = WriteStubPlugin("handler-a.slothandler.dll");
        var plugin2 = WriteStubPlugin("handler-b.slothandler.dll");
        await _sut.LaunchAsync(MakeRequest([plugin1, plugin2]), CancellationToken.None);

        var targetDir = Path.Combine(_extractedContentDir, "bin");
        Assert.That(File.Exists(Path.Combine(targetDir, "handler-a.slothandler.dll")), Is.True);
        Assert.That(File.Exists(Path.Combine(targetDir, "handler-a.slothandler.manifest.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(targetDir, "handler-b.slothandler.dll")), Is.True);
        Assert.That(File.Exists(Path.Combine(targetDir, "handler-b.slothandler.manifest.json")), Is.True);
    }

    [Test]
    public async Task WhenExecutableRelativePathHasNoDirectory_FilesAreCopiedToExtractedContentDirectoryRoot()
    {
        // Override manifest: flat executable (no subdirectory)
        File.Delete(Path.Combine(_extractedContentDir, "package-manifest.json"));
        WriteManifest(_extractedContentDir, "my-workflow");

        var plugin = WriteStubPlugin("flat.slothandler.dll");
        await _sut.LaunchAsync(MakeRequest([plugin]), CancellationToken.None);

        var expectedDll = Path.Combine(_extractedContentDir, "flat.slothandler.dll");
        Assert.That(File.Exists(expectedDll), Is.True, $"Expected DLL in root at {expectedDll}");
        Assert.That(Directory.GetDirectories(_extractedContentDir), Is.Empty,
            "No subdirectories should be created for flat packages.");
    }

    [Test]
    public async Task WhenSlotPluginFilesPresent_StartContainerCalledWithCreatedContainerId()
    {
        var plugin = WriteStubPlugin("my-workflow.slothandler.dll");
        string? capturedId = null;

        var mockContainers = new Mock<IContainerOperations>();
        mockContainers
            .Setup(c => c.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateContainerResponse { ID = "abc123" });
        mockContainers
            .Setup(c => c.StartContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
            .Callback<string, ContainerStartParameters, CancellationToken>((id, _, _) => capturedId = id)
            .ReturnsAsync(true);

        var client = new Mock<IDockerClient>();
        client.Setup(c => c.Containers).Returns(mockContainers.Object);
        _mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client.Object);

        await _sut.LaunchAsync(MakeRequest([plugin]), CancellationToken.None);

        Assert.That(capturedId, Is.EqualTo("abc123"));
    }
}

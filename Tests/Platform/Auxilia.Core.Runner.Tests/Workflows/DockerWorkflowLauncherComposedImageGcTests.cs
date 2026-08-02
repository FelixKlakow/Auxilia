using Auxilia.Core.Runner.Workflows;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// The composed-image sweep removes only EXPIRED, UNUSED <c>auxilia-env</c> images: fresh
/// compositions stay, anything a container still references stays, and the sweep can be
/// disabled entirely. Content-addressing makes removal safe (rebuild on demand).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class DockerWorkflowLauncherComposedImageGcTests
{
    private Mock<IImageOperations> _images = null!;
    private Mock<IContainerOperations> _containers = null!;
    private List<string> _deleted = null!;

    private DockerWorkflowLauncher NewSut(double maxAgeDays)
    {
        var client = new Mock<IDockerClient>();
        client.Setup(c => c.Images).Returns(_images.Object);
        client.Setup(c => c.Containers).Returns(_containers.Object);
        var factory = new Mock<IDockerClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client.Object);
        return new DockerWorkflowLauncher(
            Options.Create(new DockerWorkflowLauncherSettings { ComposedImageMaxAgeDays = maxAgeDays }),
            factory.Object,
            NullLogger<DockerWorkflowLauncher>.Instance);
    }

    [SetUp]
    public void SetUp()
    {
        _deleted = [];
        _images = new Mock<IImageOperations>();
        _images
            .Setup(i => i.DeleteImageAsync(It.IsAny<string>(), It.IsAny<ImageDeleteParameters>(), It.IsAny<CancellationToken>()))
            .Callback<string, ImageDeleteParameters, CancellationToken>((name, _, _) => _deleted.Add(name))
            .ReturnsAsync([]);
        _containers = new Mock<IContainerOperations>();
        _containers
            .Setup(c => c.ListContainersAsync(It.IsAny<ContainersListParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private void SetUpImages(params ImagesListResponse[] images)
        => _images
            .Setup(i => i.ListImagesAsync(It.IsAny<ImagesListParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(images);

    private static ImagesListResponse Image(string id, string tag, DateTime created)
        => new() { ID = id, RepoTags = [tag], Created = created };

    [Test]
    public async Task Sweep_RemovesOnlyExpiredUnusedImages()
    {
        var expired = Image("sha256:old", "auxilia-env:aaaa", DateTime.UtcNow.AddDays(-30));
        var fresh = Image("sha256:new", "auxilia-env:bbbb", DateTime.UtcNow.AddDays(-1));
        SetUpImages(expired, fresh);

        var removed = await NewSut(maxAgeDays: 14).SweepComposedImagesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(_deleted, Is.EqualTo(new[] { "auxilia-env:aaaa" }), "only the expired image goes");
        });
    }

    [Test]
    public async Task Sweep_KeepsExpiredImages_StillReferencedByAContainer()
    {
        SetUpImages(Image("sha256:old", "auxilia-env:aaaa", DateTime.UtcNow.AddDays(-30)));
        _containers
            .Setup(c => c.ListContainersAsync(It.IsAny<ContainersListParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ContainerListResponse { ImageID = "sha256:old" }]);

        var removed = await NewSut(maxAgeDays: 14).SweepComposedImagesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(_deleted, Is.Empty, "an image with a container on it must survive, however old");
        });
    }

    [Test]
    public async Task Sweep_Disabled_TouchesNothing()
    {
        var removed = await NewSut(maxAgeDays: 0).SweepComposedImagesAsync();

        Assert.That(removed, Is.Zero);
        _images.Verify(
            i => i.ListImagesAsync(It.IsAny<ImagesListParameters>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

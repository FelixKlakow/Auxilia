using Auxilia.Core.Runner.Workflows;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// An approved image is approved by digest, and the runner starts exactly that content: the
/// image carrying the registry digest (pulled by the digest when absent) or, for a local build
/// without a registry digest, the image whose ID is the digest. Whatever the coordinate's tag
/// points to now is never consulted — a re-pushed tag cannot smuggle new code under an approval.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class DockerWorkflowLauncherPinnedImageTests
{
    private static readonly string Digest = "sha256:" + new string('c', 64);
    private static readonly string Coordinate = $"registry.example:5000/team/workflow:stable@{Digest}";
    private const string Repository = "registry.example:5000/team/workflow";

    private Mock<IImageOperations> _images = null!;
    private Mock<IDockerClient> _client = null!;
    private DockerWorkflowLauncher _sut = null!;
    private readonly HashSet<string> _present = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

    [SetUp]
    public void SetUp()
    {
        _present.Clear();
        _ids.Clear();
        _images = new Mock<IImageOperations>();
        _images.Setup(i => i.InspectImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((name, _) => _present.Contains(name)
                ? Task.FromResult(new ImageInspectResponse { ID = _ids.GetValueOrDefault(name, "sha256:" + new string('f', 64)) })
                : throw new DockerImageNotFoundException(System.Net.HttpStatusCode.NotFound, name));
        _client = new Mock<IDockerClient>();
        _client.Setup(c => c.Images).Returns(_images.Object);
        _sut = new DockerWorkflowLauncher(
            Options.Create(new DockerWorkflowLauncherSettings()),
            Mock.Of<IDockerClientFactory>(),
            new FakePodHost(),
            new FakeEgressHost(),
            NullLogger<DockerWorkflowLauncher>.Instance);
    }

    [Test]
    public async Task AnImageCarryingTheRegistryDigest_IsStartedByThatDigest()
    {
        _present.Add($"{Repository}@{Digest}");

        Assert.That(await _sut.ResolvePinnedImageAsync(_client.Object, Coordinate, CancellationToken.None),
            Is.EqualTo($"{Repository}@{Digest}"));
        NeverConsultedTheTag();
    }

    [Test]
    public async Task ALocalBuildWithoutARegistryDigest_IsStartedByItsImageId()
    {
        _present.Add(Digest);
        _ids[Digest] = Digest;

        Assert.That(await _sut.ResolvePinnedImageAsync(_client.Object, Coordinate, CancellationToken.None),
            Is.EqualTo(Digest));
        NeverConsultedTheTag();
    }

    [Test]
    public async Task AnAbsentImage_IsPulledByItsDigest_NeverByItsTag()
    {
        string? pulled = null;
        _images.Setup(i => i.CreateImageAsync(It.IsAny<ImagesCreateParameters>(), It.IsAny<AuthConfig?>(),
                It.IsAny<IProgress<JSONMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<ImagesCreateParameters, AuthConfig?, IProgress<JSONMessage>, CancellationToken>((p, _, _, _) =>
            {
                pulled = p.FromImage;
                _present.Add($"{Repository}@{Digest}");
            })
            .Returns(Task.CompletedTask);

        var resolved = await _sut.ResolvePinnedImageAsync(_client.Object, Coordinate, CancellationToken.None);

        Assert.That(pulled, Is.EqualTo($"{Repository}@{Digest}"));
        Assert.That(resolved, Is.EqualTo($"{Repository}@{Digest}"));
    }

    [Test]
    public void ARetaggedImage_IsNeverWhatRuns_WhenTheApprovedContentIsGone()
    {
        // The tag now points at other content (present locally under the tag), the approved
        // digest is nowhere to be found: the launch fails instead of running the new content.
        _present.Add($"{Repository}:stable");
        _images.Setup(i => i.CreateImageAsync(It.IsAny<ImagesCreateParameters>(), It.IsAny<AuthConfig?>(),
                It.IsAny<IProgress<JSONMessage>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerApiException(System.Net.HttpStatusCode.NotFound, "manifest unknown"));

        var refusal = Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ResolvePinnedImageAsync(_client.Object, Coordinate, CancellationToken.None));

        Assert.That(refusal!.Message, Does.Contain("not available by its approved digest"));
        NeverConsultedTheTag();
    }

    [Test]
    public void AnUnpinnedCoordinate_NeverRuns()
        => Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ResolvePinnedImageAsync(_client.Object, $"{Repository}:stable", CancellationToken.None));

    [TestCase("registry.example:5000/team/workflow:stable", "registry.example:5000/team/workflow")]
    [TestCase("registry.example:5000/team/workflow", "registry.example:5000/team/workflow")]
    [TestCase("workflow:1.0", "workflow")]
    public void TheTagIsStripped_ButNeverARegistryPort(string name, string repository)
        => Assert.That(DockerWorkflowLauncher.WithoutTag(name), Is.EqualTo(repository));

    private void NeverConsultedTheTag()
        => _images.Verify(i => i.InspectImageAsync(
            It.Is<string>(n => n.EndsWith(":stable", StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.Never);
}

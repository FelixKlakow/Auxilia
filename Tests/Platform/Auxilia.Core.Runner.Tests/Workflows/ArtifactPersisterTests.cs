using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Artifacts;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Workflows;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class ArtifactPersisterTests
{
    private string _tempRoot = null!;
    private WorkflowDispatcherSettings _settings = null!;
    private Mock<IMessageBusClient> _mockBus = null!;
    private IArtifactStore _artifactStore = null!;
    private InMemoryDataAccess<AuditRecord> _audit = null!;
    private ArtifactPersister _sut = null!;
    private Guid _instanceId;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"auxilia-artifact-persister-{Guid.NewGuid():N}");
        _settings = new WorkflowDispatcherSettings
        {
            RunOutputDirectory = Path.Combine(_tempRoot, "run-output")
        };
        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _artifactStore = TestStores.NewArtifactStore(Path.Combine(_tempRoot, "platform-data"));
        _audit = new InMemoryDataAccess<AuditRecord>();
        _sut = new ArtifactPersister(
            _artifactStore,
            _mockBus.Object,
            new AuditLog(_audit, TimeProvider.System),
            TimeProvider.System,
            Options.Create(_settings),
            NullLogger<ArtifactPersister>.Instance);
        _instanceId = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown()
    {
        _audit.Dispose();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private string CreateOutputDirectory()
    {
        var dir = _sut.OutputDirectoryFor(_instanceId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string OutputsJson(params WorkflowOutputDescriptor[] outputs)
        => JsonSerializer.Serialize(outputs.ToList());

    [Test]
    public async Task DeclaredOutputPresent_SavesAuditsAndPublishes_LeavingTheSweepToRunRootsCleanup()
    {
        var outputDir = CreateOutputDirectory();
        await File.WriteAllTextAsync(Path.Combine(outputDir, "result.json"), """{"verdict":"approve"}""");

        ArtifactPersistedEvent? published = null;
        string? publishedKey = null;
        _mockBus
            .Setup(b => b.DeclareTopicExchangeAsync("workflow.artifacts", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishToTopicExchangeAsync(
                "workflow.artifacts", It.IsAny<string>(), It.IsAny<ArtifactPersistedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, ArtifactPersistedEvent, CancellationToken>((_, key, e, _) =>
            {
                publishedKey = key;
                published = e;
            })
            .Returns(Task.CompletedTask);

        await _sut.PersistOutputsAsync(
            _instanceId, "review-workflow",
            OutputsJson(new WorkflowOutputDescriptor("review-result", "result.json", null)),
            "WI-7");

        var lineage = await _artifactStore.GetLineageAsync("review-result", "WI-7");
        Assert.That(lineage, Has.Count.EqualTo(1));
        Assert.That(lineage[0].WorkflowType, Is.EqualTo("review-workflow"));
        Assert.That(lineage[0].RunInstanceId, Is.EqualTo(_instanceId));
        Assert.That(lineage[0].Version, Is.EqualTo(1));

        _mockBus.Verify(b => b.PublishToTopicExchangeAsync(
            "workflow.artifacts", It.IsAny<string>(), It.IsAny<ArtifactPersistedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(publishedKey, Is.EqualTo("review-result"),
            "The routing key must be the artifact type so subscribers can bind selectively.");
        Assert.That(published, Is.Not.Null);
        Assert.That(published!.ArtifactId, Is.EqualTo(lineage[0].Id));
        Assert.That(published.ContentHash, Is.EqualTo(lineage[0].ContentHash));
        Assert.That(published.WorkItemId, Is.EqualTo("WI-7"));

        var auditQuery = await _audit.ReadAsync();
        Assert.That(auditQuery.Any(a => a.Action == "artifact.persisted"), Is.True,
            "Persisting an artifact must be audited.");

        Assert.That(Directory.Exists(outputDir), Is.True,
            "Persistence must not sweep the output root itself — RunRootsCleanup does, on every terminal path.");
    }

    [Test]
    public async Task MissingOutputFile_IsSkippedWithoutEvent()
    {
        CreateOutputDirectory(); // exists, but the declared file was never produced

        await _sut.PersistOutputsAsync(
            _instanceId, "review-workflow",
            OutputsJson(new WorkflowOutputDescriptor("review-result", "missing.json", null)),
            "WI-7");

        Assert.That(await _artifactStore.GetLineageAsync("review-result", "WI-7"), Is.Empty);
        _mockBus.VerifyNoOtherCalls();
    }

    [Test]
    public async Task PathEscapingRelativePath_IsSkippedWithoutSave()
    {
        var outputDir = CreateOutputDirectory();
        // The escape target exists, so only the path check can prevent persisting it.
        await File.WriteAllTextAsync(
            Path.GetFullPath(Path.Combine(outputDir, "..", "evil.json")), "outside payload");

        await _sut.PersistOutputsAsync(
            _instanceId, "review-workflow",
            OutputsJson(new WorkflowOutputDescriptor("evil", "../evil.json", null)),
            "WI-7");

        Assert.That(await _artifactStore.GetLineageAsync("evil", "WI-7"), Is.Empty,
            "An output escaping the run's output directory must never be persisted.");
        _mockBus.VerifyNoOtherCalls();
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task NullOrEmptyOutputsJson_IsANoOp(string? outputsJson)
    {
        var outputDir = CreateOutputDirectory();

        await _sut.PersistOutputsAsync(_instanceId, "review-workflow", outputsJson, "WI-7");

        Assert.That(Directory.Exists(outputDir), Is.True,
            "Without declared outputs nothing may be persisted or deleted.");
        _mockBus.VerifyNoOtherCalls();
    }
}

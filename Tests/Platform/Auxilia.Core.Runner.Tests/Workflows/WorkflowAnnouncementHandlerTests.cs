using Auxilia.Messaging;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowAnnouncementHandlerTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<PendingWorkflowPackageStore> _mockPendingPackages = null!;
    private WorkflowSchemaStore _schemaStore = null!;
    private Func<WorkflowAnnouncementMessage, CancellationToken, Task>? _capturedHandler;
    private WorkflowAnnouncementHandler _sut = null!;
    private WorkflowInstanceTokenRegistry _tokenRegistry = null!;

    [SetUp]
    public async Task SetUp()
    {
        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _mockPendingPackages = new Mock<PendingWorkflowPackageStore>();
        _schemaStore = TestStores.NewWorkflowSchemaStore();

        string? outPath;
        _mockPendingPackages
            .Setup(p => p.TryConsume(It.IsAny<string>(), out outPath))
            .Returns(false);

        var disposable = new Mock<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.DeclareQueueAsync("workflow.announcements", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockBus
            .Setup(b => b.SubscribeAsync<WorkflowAnnouncementMessage>(
                "workflow.announcements",
                It.IsAny<Func<WorkflowAnnouncementMessage, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<WorkflowAnnouncementMessage, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(disposable.Object);

        _mockBus
            .Setup(b => b.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<WorkflowDirective>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var settings = Options.Create(new WorkflowDispatcherSettings { RequireInstanceToken = false });
        _tokenRegistry = new WorkflowInstanceTokenRegistry(settings, TimeProvider.System);
        _sut = new WorkflowAnnouncementHandler(
            _mockBus.Object,
            NullLogger<WorkflowAnnouncementHandler>.Instance,
            _schemaStore,
            _mockPendingPackages.Object,
            _tokenRegistry,
            settings);

        await _sut.StartAsync(CancellationToken.None);
    }

    private async Task<WorkflowAnnouncementHandler> MakeTokenRequiringHandlerAsync()
    {
        await _sut.StopAsync();
        var settings = Options.Create(new WorkflowDispatcherSettings { RequireInstanceToken = true });
        var sut = new WorkflowAnnouncementHandler(
            _mockBus.Object,
            NullLogger<WorkflowAnnouncementHandler>.Instance,
            _schemaStore,
            _mockPendingPackages.Object,
            _tokenRegistry,
            settings);
        await sut.StartAsync(CancellationToken.None);
        _sut = sut;
        return sut;
    }

    [TearDown]
    public async Task TearDown() => await _sut.StopAsync();

    [Test]
    public void WhenStartCalled_DeclaresAnnouncementsQueue()
    {
        _mockBus.Verify(
            b => b.DeclareQueueAsync("workflow.announcements", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void WhenStartCalled_SubscribesToAnnouncementsQueue()
    {
        _mockBus.Verify(
            b => b.SubscribeAsync<WorkflowAnnouncementMessage>(
                "workflow.announcements",
                It.IsAny<Func<WorkflowAnnouncementMessage, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenAnnouncementReceived_PublishesRunDirectiveToResponseTopic()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var message = new WorkflowAnnouncementMessage(instanceId, "test-workflow", "pubkey", "reply-topic-123");

        // Act
        await _capturedHandler!(message, CancellationToken.None);

        // Assert
        _mockBus.Verify(
            b => b.PublishAsync(
                "reply-topic-123",
                It.Is<WorkflowDirective>(d =>
                    d.WorkflowInstanceId == instanceId &&
                    d.Directive == WorkflowDirectiveKind.Run),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenAnnouncementReceived_DirectiveCarriesCorrectInstanceId()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        WorkflowDirective? captured = null;
        _mockBus
            .Setup(b => b.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<WorkflowDirective>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, WorkflowDirective, CancellationToken>((_, d, _) => captured = d)
            .Returns(Task.CompletedTask);

        var message = new WorkflowAnnouncementMessage(instanceId, "wf", "pk", "topic");

        // Act
        await _capturedHandler!(message, CancellationToken.None);

        // Assert
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.WorkflowInstanceId, Is.EqualTo(instanceId));
        Assert.That(captured.Directive, Is.EqualTo(WorkflowDirectiveKind.Run));
    }

    [Test]
    public async Task WhenMultipleAnnouncementsReceived_EachGetsItsOwnDirective()
    {
        // Arrange
        var published = new List<(string Topic, WorkflowDirective Directive)>();
        _mockBus
            .Setup(b => b.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<WorkflowDirective>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, WorkflowDirective, CancellationToken>(
                (topic, d, _) => published.Add((topic, d)))
            .Returns(Task.CompletedTask);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // Act
        await _capturedHandler!(
            new WorkflowAnnouncementMessage(id1, "wf", "pk", "topic-1"), CancellationToken.None);
        await _capturedHandler!(
            new WorkflowAnnouncementMessage(id2, "wf", "pk", "topic-2"), CancellationToken.None);

        // Assert
        Assert.That(published, Has.Count.EqualTo(2));
        Assert.That(published[0].Topic, Is.EqualTo("topic-1"));
        Assert.That(published[0].Directive.WorkflowInstanceId, Is.EqualTo(id1));
        Assert.That(published[1].Topic, Is.EqualTo("topic-2"));
        Assert.That(published[1].Directive.WorkflowInstanceId, Is.EqualTo(id2));
    }

    [Test]
    public async Task WhenPendingPackageExists_SetsSchemaBeforeSendingRunDirective()
    {
        // Arrange
        var workflowName = "my-workflow";
        var instanceId = Guid.NewGuid();

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var schemaJson = """
                {
                    "workflowName": "my-workflow",
                    "slots": [],
                    "environmentRequirements": []
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(tempDir, "workflow-schema.json"), schemaJson);

            string? outPath = tempDir;
            _mockPendingPackages
                .Setup(p => p.TryConsume(workflowName, out outPath))
                .Returns(true);

            var message = new WorkflowAnnouncementMessage(instanceId, workflowName, "pubkey", "reply-topic");

            // Act
            await _capturedHandler!(message, CancellationToken.None);

            // Assert
            Assert.That(await _schemaStore.GetSchemaAsync(workflowName), Is.Not.Null,
                "Schema should have been pre-loaded from the pending package.");
            _mockBus.Verify(
                b => b.PublishAsync(
                    "reply-topic",
                    It.Is<WorkflowDirective>(d => d.Directive == WorkflowDirectiveKind.Run),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ Instance token authentication

    [Test]
    public async Task WhenTokenRequired_AndTokenMissing_NoDirectiveIsPublished()
    {
        await MakeTokenRequiringHandlerAsync();

        var message = new WorkflowAnnouncementMessage(Guid.NewGuid(), "wf", "pk", "self-declared");
        await _capturedHandler!(message, CancellationToken.None);

        _mockBus.Verify(
            b => b.PublishAsync(It.IsAny<string>(), It.IsAny<WorkflowDirective>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task WhenTokenRequired_AndTokenInvalid_NoDirectiveIsPublished()
    {
        var issued = _tokenRegistry.Issue("wf");
        await MakeTokenRequiringHandlerAsync();

        var message = new WorkflowAnnouncementMessage(
            issued.WorkflowInstanceId, "wf", "pk", "self-declared", "wrong-token");
        await _capturedHandler!(message, CancellationToken.None);

        _mockBus.Verify(
            b => b.PublishAsync(It.IsAny<string>(), It.IsAny<WorkflowDirective>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task WhenTokenRequired_AndTokenValid_DirectiveGoesToCanonicalQueueNotSelfDeclaredTopic()
    {
        var issued = _tokenRegistry.Issue("wf");
        await MakeTokenRequiringHandlerAsync();

        var message = new WorkflowAnnouncementMessage(
            issued.WorkflowInstanceId, "wf", "pk", "self-declared-attacker-topic", issued.Token);
        await _capturedHandler!(message, CancellationToken.None);

        _mockBus.Verify(
            b => b.PublishAsync(
                WorkflowQueues.ResponseQueueFor(issued.WorkflowInstanceId),
                It.Is<WorkflowDirective>(d => d.Directive == WorkflowDirectiveKind.Run),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _mockBus.Verify(
            b => b.PublishAsync(
                "self-declared-attacker-topic", It.IsAny<WorkflowDirective>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task WhenNoPendingPackageExists_StillSendsRunDirective()
    {
        // Arrange
        var workflowName = "unknown-workflow";
        var instanceId = Guid.NewGuid();
        var message = new WorkflowAnnouncementMessage(instanceId, workflowName, "pubkey", "reply-topic");

        // Act
        await _capturedHandler!(message, CancellationToken.None);

        // Assert
        Assert.That(await _schemaStore.GetSchemaAsync(workflowName), Is.Null,
            "No schema should have been stored when no pending package exists.");
        _mockBus.Verify(
            b => b.PublishAsync(
                "reply-topic",
                It.Is<WorkflowDirective>(d => d.Directive == WorkflowDirectiveKind.Run),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}


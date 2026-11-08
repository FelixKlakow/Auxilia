using Auxilia.Messaging;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// What a run brings beyond its context: tools it needs the image to provide (checked before
/// launch — a mismatch is PreFlightFailed, never a run on another version) and input files the
/// runner fetches from the Core into the run's own output bind.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkflowDispatcherRunInputTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<IWorkflowLauncher> _mockLauncher = null!;
    private WorkflowSchemaStore _schemaStore = null!;
    private FakeInputFileSource _inputFiles = null!;
    private Func<RunWorkflowCommand, CancellationToken, Task>? _handler;
    private WorkflowLaunchRequest? _launched;
    private readonly List<WorkflowStatusEvent> _statuses = [];
    private string _outputRoot = "";
    private WorkflowDispatcher _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _statuses.Clear();
        _launched = null;
        _outputRoot = Path.Combine(Path.GetTempPath(), $"run-outputs-{Guid.NewGuid():N}");
        _mockBus = new Mock<IMessageBusClient>();
        _mockBus
            .Setup(b => b.SubscribeAsync<RunWorkflowCommand>(
                It.IsAny<string>(), It.IsAny<Func<RunWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<RunWorkflowCommand, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _handler = handler)
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        _mockBus
            .Setup(b => b.PublishToTopicExchangeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorkflowStatusEvent, CancellationToken>((_, _, status, _) => _statuses.Add(status))
            .Returns(Task.CompletedTask);

        _mockLauncher = new Mock<IWorkflowLauncher>();
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((request, _) => _launched = request)
            .ReturnsAsync(new WorkflowLaunchResult());

        _schemaStore = TestStores.NewWorkflowSchemaStore();
        await _schemaStore.SetSchemaAsync("my-workflow", new WorkflowSchema("my-workflow", [], [])
        {
            ProvidedTools = ["agent-cli@1.0.107"],
            InputFiles = [new WorkflowInputFileDescriptor("session-state", Required: true)],
        });
        _inputFiles = new FakeInputFileSource();
        var dispatcherSettings = Options.Create(new WorkflowDispatcherSettings { RunOutputDirectory = _outputRoot });
        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(new DockerWorkflowLauncherSettings
            {
                NetworkName = "test-net",
                RabbitMqHost = "rabbitmq",
                RabbitMqPort = 5672,
                ExtraEnvironmentVariables = new Dictionary<string, string>
                {
                    [WorkflowEnvironmentVariables.InputFilePrefix + "SESSION-STATE"] = "/somewhere/else",
                },
            }),
            dispatcherSettings,
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<IWorkflowPackageVerifier>(),
            new Mock<PendingWorkflowPackageStore>().Object,
            TestStores.NewSlotProviderRegistry(),
            new WorkflowInstanceTokenRegistry(dispatcherSettings, TimeProvider.System),
            TestStores.NewPolicyEngine(),
            TestStores.NewWorkflowInstanceRegistry(),
            TestStores.NewStatusPublisher(_mockBus.Object),
            _schemaStore,
            TestStores.NewWorkflowPackageStore(),
            TestStores.NewArtifactStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(),
            TestStores.NewHostPlatformProbe(),
            Mock.Of<IRepositoryAuthResolver>(),
            TestStores.NewAuditLog(),
            TestStores.NewInstanceInfo(),
            new Auxilia.PlatformData.Protection.NullSettingsProtector(),
            new FakePodHost(),
            new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            TestStores.NewContainment(),
            NullLogger<WorkflowDispatcher>.Instance,
            _inputFiles);
        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync();
        if (Directory.Exists(_outputRoot))
            Directory.Delete(_outputRoot, recursive: true);
    }

    private static RunWorkflowCommand Command(
        IReadOnlyList<string>? requiredTools = null, IReadOnlyList<string>? inputFiles = null)
        => new(Guid.NewGuid(), "my-workflow",
            "docker://my-image:1.0@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new Dictionary<string, string>(), ResolutionToken: "run-token",
            InputFiles: inputFiles, RequiredTools: requiredTools);

    [Test]
    public async Task ARunThatNeedsAnotherToolVersion_EndsPreFlightFailed_AndNeverLaunches()
    {
        await _handler!(Command(requiredTools: ["agent-cli@1.0.95"]), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_launched, Is.Null, "it never runs on another version");
            var failed = _statuses.Single(s => s.State == "PreFlightFailed");
            Assert.That(failed.ErrorMessage, Does.Contain("agent-cli@1.0.95").And.Contain("agent-cli@1.0.107"),
                "the reason names what was needed and what the image has");
        });
    }

    [TestCase("agent-cli@1.0.107")]
    [TestCase("agent-cli")]
    public async Task ARunWhoseToolsTheImageProvides_Launches(string required)
    {
        await _handler!(Command(requiredTools: [required]), CancellationToken.None);

        Assert.That(_launched, Is.Not.Null);
    }

    [Test]
    public async Task AnInputFile_IsFetchedWithTheRunsToken_IntoTheRunsOwnOutputBind()
    {
        var command = Command(inputFiles: ["session-state"]);

        await _handler!(command, CancellationToken.None);

        Assert.That(_launched, Is.Not.Null);
        var (runId, token, name, destination) = _inputFiles.Fetches.Single();
        Assert.Multiple(() =>
        {
            Assert.That((runId, token, name), Is.EqualTo((command.CommandId, "run-token", "session-state")));
            Assert.That(destination, Does.StartWith(_outputRoot).And.EndWith(Path.Combine("inputs", "session-state")));
            Assert.That(File.ReadAllText(destination), Is.EqualTo("content of session-state"));
            Assert.That(_launched!.EnvironmentVariables[WorkflowEnvironmentVariables.InputFileVariable("session-state")],
                Is.EqualTo("/workflow-output/inputs/session-state"),
                "the workflow finds it through its variable — no launch extra can repoint it");
        });
    }

    [Test]
    public async Task AnInputFileTheCoreDoesNotServe_EndsPreFlightFailed()
    {
        _inputFiles.Serves = false;

        await _handler!(Command(inputFiles: ["session-state"]), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_launched, Is.Null);
            Assert.That(_statuses.Single(s => s.State == "PreFlightFailed").ErrorMessage, Does.Contain("session-state"));
        });
    }

    private sealed class FakeInputFileSource : IRunInputFileSource
    {
        public bool Serves { get; set; } = true;
        public List<(Guid RunId, string Token, string Name, string Destination)> Fetches { get; } = [];

        public async Task<bool> DownloadAsync(
            Guid coreRunId, string resolutionToken, string name, string destinationPath, CancellationToken ct)
        {
            Fetches.Add((coreRunId, resolutionToken, name, destinationPath));
            if (!Serves)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllTextAsync(destinationPath, $"content of {name}", ct);
            return true;
        }
    }
}

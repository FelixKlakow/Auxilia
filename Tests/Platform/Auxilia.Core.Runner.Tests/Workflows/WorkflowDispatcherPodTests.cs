using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Companions;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Pod dispatch (run-pod design §A): a schema-declared companion topology is resolved
/// against the run's inputs into the launch request's <see cref="PodPlan"/>, its facts are
/// announced to the workflow container, and a structurally invalid topology fails the run
/// pre-flight instead of launching.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkflowDispatcherPodTests
{
    private const string Image = "img@sha256:3333333333333333333333333333333333333333333333333333333333333333";

    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<IWorkflowLauncher> _mockLauncher = null!;
    private WorkflowInstanceRegistry _instanceRegistry = null!;
    private Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry _podControlRegistry = null!;
    private Func<RunWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private WorkflowLaunchRequest? _capturedRequest;
    private WorkflowDispatcher _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _capturedRequest = null;
        _mockBus = new Mock<IMessageBusClient>();
        _mockBus
            .Setup(b => b.SubscribeAsync<RunWorkflowCommand>(
                It.IsAny<string>(),
                It.IsAny<Func<RunWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<RunWorkflowCommand, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());

        _mockLauncher = new Mock<IWorkflowLauncher>();
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => _capturedRequest = req)
            .ReturnsAsync(new WorkflowLaunchResult());

        _instanceRegistry = TestStores.NewWorkflowInstanceRegistry();
        _podControlRegistry = new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry();
        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(new DockerWorkflowLauncherSettings
            {
                NetworkName = "test-net",
                RabbitMqHost = "rabbitmq",
                RabbitMqPort = 5672,
                RabbitMqUserName = "guest",
                RabbitMqPassword = "guest"
            }),
            Options.Create(new WorkflowDispatcherSettings()),
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<IWorkflowPackageVerifier>(),
            new Mock<PendingWorkflowPackageStore>().Object,
            TestStores.NewSlotProviderRegistry(),
            new WorkflowInstanceTokenRegistry(
                Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System),
            TestStores.NewPolicyEngine(),
            _instanceRegistry,
            TestStores.NewStatusPublisher(_mockBus.Object),
            TestStores.NewWorkflowSchemaStore(),
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
            _podControlRegistry,
            NullLogger<WorkflowDispatcher>.Instance);
        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown() => await _sut.StopAsync();

    private static RunWorkflowCommand Command(
        IReadOnlyList<CompanionDeclaration> companions,
        Dictionary<string, string>? context = null)
        => new(Guid.NewGuid(), "pod-workflow", "docker://pod-workflow:1",
            context ?? new Dictionary<string, string>(),
            SchemaJson: JsonSerializer.Serialize(
                new WorkflowSchema("pod-workflow", [], []) { Companions = companions }));

    [Test]
    public async Task DeclaredCompanions_RideTheLaunchRequest_WithAnnouncedFacts()
    {
        await _capturedHandler!(Command(
            [
                new CompanionDeclaration("rabbit", Image)
                {
                    Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 5672)
                },
                new CompanionDeclaration("machine", Image)
                {
                    MinInstances = 0, MaxInstances = 8, CountInput = "machines",
                    StartAfter = ["rabbit"]
                }
            ],
            new Dictionary<string, string> { ["machines"] = "3" }), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_capturedRequest, Is.Not.Null);
            var pod = _capturedRequest!.Pod;
            Assert.That(pod, Is.Not.Null, "the launch request must carry the resolved pod");
            Assert.That(pod!.Companions.Select(c => c.InstanceName),
                Is.EquivalentTo(new[] { "rabbit", "machine-1", "machine-2", "machine-3" }));
            Assert.That(_capturedRequest.EnvironmentVariables["Workflow__Companion__MACHINE__COUNT"],
                Is.EqualTo("3"), "the workflow container learns the resolved topology");
            Assert.That(_capturedRequest.EnvironmentVariables["Workflow__Companion__RABBIT__ENDPOINTS"],
                Is.EqualTo("rabbit:5672"));
        });
    }

    [Test]
    public async Task InvalidTopology_FailsPreFlight_InsteadOfLaunching()
    {
        var command = Command([new CompanionDeclaration("db", "postgres:17")]);

        await _capturedHandler!(command, CancellationToken.None);

        // PreFlightFailed is a terminal state, so a correctly failed dispatch leaves no
        // non-terminal record behind — and above all, no launch ever happened.
        var nonTerminal = await _instanceRegistry.GetNonTerminalAsync();
        Assert.Multiple(() =>
        {
            Assert.That(_capturedRequest, Is.Null, "an unpinned companion image must never launch");
            Assert.That(nonTerminal, Is.Empty, "the run must end PreFlightFailed, not linger");
        });
    }

    [Test]
    public async Task NoCompanions_MeansNoPod()
    {
        await _capturedHandler!(Command([]), CancellationToken.None);

        Assert.That(_capturedRequest!.Pod, Is.Null);
    }

    [Test]
    public async Task PodControlEnvelope_RegistersStateFromTheConfigurationPinnedBaseMap()
    {
        var baseMap = new Dictionary<string, string>
        {
            ["sim-base"] = Image,
            ["sim-base/24.04"] = Image
        };
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "pod-workflow", "docker://pod-workflow:1",
            new Dictionary<string, string>(),
            SchemaJson: JsonSerializer.Serialize(new WorkflowSchema("pod-workflow", [], [])
            {
                PodControl = new PodControlDeclaration(16) { PodVolumes = ["bin"] }
            }),
            PodBaseImagesJson: JsonSerializer.Serialize(baseMap));

        await _capturedHandler!(command, CancellationToken.None);

        var pod = _capturedRequest!.Pod!;
        var state = _podControlRegistry.Get(pod.InstanceId);
        Assert.Multiple(() =>
        {
            Assert.That(pod.Companions, Is.Empty,
                "no declared companions — the pod exists for the envelope alone");
            Assert.That(_capturedRequest.EnvironmentVariables["Workflow__PodControlQueue"],
                Is.EqualTo("workflow-pod-control"));
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.MaxContainers, Is.EqualTo(16));
            Assert.That(state.BaseImages, Is.EquivalentTo(baseMap));
            Assert.That(state.NetworkName, Is.EqualTo(pod.NetworkName));
            Assert.That(state.VolumeNames["bin"], Is.EqualTo($"auxilia-pod-{pod.InstanceId:N}-bin"));
        });
    }

    [Test]
    public async Task PodControl_WithoutPinnedBases_RegistersAnEmptyMap()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "pod-workflow", "docker://pod-workflow:1",
            new Dictionary<string, string>(),
            SchemaJson: JsonSerializer.Serialize(new WorkflowSchema("pod-workflow", [], [])
            {
                PodControl = new PodControlDeclaration(4)
            }));

        await _capturedHandler!(command, CancellationToken.None);

        var state = _podControlRegistry.Get(_capturedRequest!.Pod!.InstanceId);
        Assert.That(state!.BaseImages, Is.Empty,
            "no configuration-pinned bases = default-deny; every spawn's base resolution fails");
    }
}

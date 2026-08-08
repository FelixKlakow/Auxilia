using System.Text.Json;
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
/// The first-dispatch schema gap (backlog "Session terminal remainders"): a runner that has
/// never run a type has no stored schema, so schema-driven pre-launch decisions (the
/// interactive terminal above all) silently came out empty on the FIRST run. The dispatch
/// command now carries the registry's inspected schema as a cold-start seed — these tests
/// prove the first dispatch decides the terminal from it, persists it, and that the runner's
/// own store still wins once populated.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkflowDispatcherSchemaSeedTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<IWorkflowLauncher> _mockLauncher = null!;
    private WorkflowSchemaStore _schemaStore = null!;
    private Func<RunWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private WorkflowLaunchRequest? _capturedRequest;
    private WorkflowDispatcher _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
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

        _schemaStore = TestStores.NewWorkflowSchemaStore();
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
            TestStores.NewWorkflowInstanceRegistry(),
            TestStores.NewStatusPublisher(_mockBus.Object),
            _schemaStore,
            TestStores.NewWorkflowPackageStore(),
            TestStores.NewArtifactStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(),
            Mock.Of<IRepositoryAuthResolver>(),
            TestStores.NewAuditLog(),
            TestStores.NewInstanceInfo(),
            new Auxilia.PlatformData.Protection.NullSettingsProtector(),
            NullLogger<WorkflowDispatcher>.Instance);
        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown() => await _sut.StopAsync();

    private static string TerminalSchemaJson(int port = 7681, JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(
            new WorkflowSchema("my-workflow", [], []) { InteractiveTerminalPort = port }, options);

    private static RunWorkflowCommand Command(string? schemaJson)
        => new(Guid.NewGuid(), "my-workflow", "docker://my-image:1.0",
            new Dictionary<string, string>(), SchemaJson: schemaJson);

    [Test]
    public async Task FirstDispatchOfAFreshType_DecidesTheTerminalFromTheCommandSchema_AndPersistsIt()
    {
        await _capturedHandler!(Command(TerminalSchemaJson()), CancellationToken.None);

        var stored = await _schemaStore.GetSchemaAsync("my-workflow");
        Assert.Multiple(() =>
        {
            Assert.That(_capturedRequest, Is.Not.Null);
            Assert.That(_capturedRequest!.PublishTerminalPort, Is.EqualTo(7681),
                "the FIRST run of a freshly registered type must not lose its terminal");
            Assert.That(_capturedRequest.TerminalContainerName, Is.Not.Null.And.Not.Empty);
            Assert.That(stored?.InteractiveTerminalPort, Is.EqualTo(7681),
                "the seed persists, so later store readers see the schema too");
        });
    }

    [Test]
    public async Task CamelCaseRegistrySchema_SeedsJustTheSame()
    {
        // Packer-produced schemas are camelCase; runner announcements are PascalCase.
        var camel = TerminalSchemaJson(options: JsonSerializerOptions.Web);

        await _capturedHandler!(Command(camel), CancellationToken.None);

        Assert.That(_capturedRequest!.PublishTerminalPort, Is.EqualTo(7681));
    }

    [Test]
    public async Task StoredSchema_WinsOverTheCommandSeed()
    {
        // The store is refreshed by every run's own registration — it is the more current
        // truth for this runner; the command schema is only the cold-start seed.
        await _schemaStore.SetSchemaAsync("my-workflow", new WorkflowSchema("my-workflow", [], []));

        await _capturedHandler!(Command(TerminalSchemaJson()), CancellationToken.None);

        Assert.That(_capturedRequest!.PublishTerminalPort, Is.Null,
            "a populated store must not be overridden by the dispatch seed");
    }

    [Test]
    public async Task MalformedCommandSchema_LaunchesSchemaLess_InsteadOfFailing()
    {
        await _capturedHandler!(Command("this is not json"), CancellationToken.None);

        var stored = await _schemaStore.GetSchemaAsync("my-workflow");
        Assert.Multiple(() =>
        {
            Assert.That(_capturedRequest, Is.Not.Null, "the launch must still happen");
            Assert.That(_capturedRequest!.PublishTerminalPort, Is.Null);
            Assert.That(stored, Is.Null);
        });
    }

    [Test]
    public async Task NoCommandSchema_KeepsTheOldSchemaLessBehaviour()
    {
        await _capturedHandler!(Command(schemaJson: null), CancellationToken.None);

        Assert.That(_capturedRequest!.PublishTerminalPort, Is.Null);
    }
}

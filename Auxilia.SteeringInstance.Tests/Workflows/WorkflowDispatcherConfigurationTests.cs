using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.SteeringInstance.Tests.Workflows;

/// <summary>
/// Unit tests for named-configuration dispatch (#18): the dispatcher resolves workflow type
/// and package URI from the configuration, records the configuration on the instance record,
/// and fails pre-flight when the configuration is missing, disabled, or references an
/// unregistered slot provider.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WorkflowDispatcherConfigurationTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<IWorkflowLauncher> _mockLauncher = null!;
    private Func<RunWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private WorkflowDispatcher _sut = null!;
    private WorkflowConfigurationStore _configurationStore = null!;
    private SlotProviderRegistry _providerRegistry = null!;
    private InMemoryDataAccess<WorkflowInstanceRecord> _instanceRecords = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;
    private List<WorkflowStatusEvent> _statusEvents = null!;

    [SetUp]
    public async Task SetUp()
    {
        _configurationStore = TestStores.NewWorkflowConfigurationStore();
        _providerRegistry = TestStores.NewSlotProviderRegistry();
        _instanceRecords = new InMemoryDataAccess<WorkflowInstanceRecord>();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _statusEvents = [];

        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _mockLauncher = new Mock<IWorkflowLauncher>(MockBehavior.Strict);

        var disposable = new Mock<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.DeclareQueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.DeclareExchangeAsync("workflow.status-events", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.PublishToExchangeAsync(
                "workflow.status-events", It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorkflowStatusEvent, CancellationToken>((_, evt, _) => _statusEvents.Add(evt))
            .Returns(Task.CompletedTask);
        _mockBus
            .Setup(b => b.SubscribeAsync<RunWorkflowCommand>(
                "workflow.run-commands",
                It.IsAny<Func<RunWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<RunWorkflowCommand, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(disposable.Object);

        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(new DockerWorkflowLauncherSettings
            {
                RabbitMqHost = "rabbitmq",
                RabbitMqPort = 5672,
                RabbitMqUserName = "guest",
                RabbitMqPassword = "guest"
            }),
            Options.Create(new WorkflowDispatcherSettings()),
            new Mock<IHttpClientFactory>(MockBehavior.Strict).Object,
            new Mock<IWorkflowPackageVerifier>(MockBehavior.Strict).Object,
            new Mock<PendingWorkflowPackageStore>().Object,
            TestStores.NewSlotConfigurationStore(),
            _providerRegistry,
            _configurationStore,
            new WorkflowInstanceTokenRegistry(
                Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System),
            TestStores.NewPolicyEngine(),
            new WorkflowInstanceRegistry(_instanceRecords, TimeProvider.System),
            TestStores.NewStatusPublisher(_mockBus.Object),
            TestStores.NewWorkflowSchemaStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(),
            new AuditLog(_auditRecords, TimeProvider.System),
            TestStores.NewInstanceInfo(),
            NullLogger<WorkflowDispatcher>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync();
        _instanceRecords.Dispose();
        _auditRecords.Dispose();
    }

    private Task<StoredWorkflowConfiguration> SeedConfigurationAsync(
        bool enabled = true, string providerType = "git")
        => _configurationStore.UpsertAsync(new StoredWorkflowConfiguration(
            "alpha", "Alpha", "cfg-wf", "docker://cfg-wf:test", enabled,
            [new StoredSlotBinding("repo", providerType,
                new Dictionary<string, string> { ["Url"] = "https://example.com" })]));

    private static RunWorkflowCommand ConfigurationCommand(Guid configurationId)
        => new(Guid.NewGuid(), null, null, new Dictionary<string, string>(),
            WorkflowConfigurationId: configurationId);

    private async Task<WorkflowInstanceRecord> SingleInstanceRecordAsync()
    {
        var query = await _instanceRecords.ReadAsync();
        var records = query.ToList();
        Assert.That(records, Has.Count.EqualTo(1));
        return records[0];
    }

    // ------------------------------------------------------------------ Found

    [Test]
    public async Task ConfigurationFound_LaunchesWithTypeAndPackageFromConfiguration()
    {
        var configuration = await SeedConfigurationAsync();
        await _providerRegistry.UpsertAsync("git", "/plugins/git.slothandler.dll");

        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        await _capturedHandler!(ConfigurationCommand(configuration.Id), CancellationToken.None);

        Assert.That(captured, Is.Not.Null, "Launcher must be called for a valid configuration.");
        Assert.Multiple(() =>
        {
            Assert.That(captured!.DockerImageUri, Is.EqualTo("cfg-wf:test"));
            Assert.That(captured.SlotPluginFiles, Has.Count.EqualTo(1));
            Assert.That(captured.SlotPluginFiles[0].DllPath, Is.EqualTo("/plugins/git.slothandler.dll"));
        });
    }

    [Test]
    public async Task ConfigurationFound_InstanceRecordCarriesConfigurationIdAndName()
    {
        var configuration = await SeedConfigurationAsync();
        await _providerRegistry.UpsertAsync("git", "/plugins/git.slothandler.dll");

        await _capturedHandler!(ConfigurationCommand(configuration.Id), CancellationToken.None);

        var record = await SingleInstanceRecordAsync();
        Assert.Multiple(() =>
        {
            Assert.That(record.WorkflowType, Is.EqualTo("cfg-wf"));
            Assert.That(record.State, Is.EqualTo("Queued"));
            Assert.That(record.WorkflowConfigurationId, Is.EqualTo(configuration.Id));
            Assert.That(record.WorkflowConfigurationName, Is.EqualTo("alpha"));
        });
    }

    [Test]
    public async Task ConfigurationLessDispatch_InstanceRecordCarriesNoConfiguration()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "plain-wf", "docker://plain-wf:test", new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        var record = await SingleInstanceRecordAsync();
        Assert.Multiple(() =>
        {
            Assert.That(record.WorkflowType, Is.EqualTo("plain-wf"));
            Assert.That(record.State, Is.EqualTo("Queued"));
            Assert.That(record.WorkflowConfigurationId, Is.Null);
            Assert.That(record.WorkflowConfigurationName, Is.Null);
        });
    }

    // ------------------------------------------------------------------ Pre-flight failures

    private async Task AssertPreFlightFailedAsync(string expectedReasonFragment)
    {
        _mockLauncher.Verify(
            l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var record = await SingleInstanceRecordAsync();
        Assert.That(record.State, Is.EqualTo("PreFlightFailed"));
        Assert.That(record.ErrorMessage, Does.Contain(expectedReasonFragment));

        Assert.That(_statusEvents.Select(e => e.State),
            Is.EqualTo(new[] { "Received", "PreFlightFailed" }));

        var query = await _auditRecords.ReadAsync();
        var entries = query.Where(r => r.Action == "workflow.dispatch.rejected").ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Outcome, Does.Contain(expectedReasonFragment));
    }

    [Test]
    public async Task ConfigurationMissing_FailsPreFlightWithStatusEventAndAudit()
    {
        await _capturedHandler!(ConfigurationCommand(Guid.NewGuid()), CancellationToken.None);

        await AssertPreFlightFailedAsync("not found");
    }

    [Test]
    public async Task ConfigurationDisabled_FailsPreFlightWithStatusEventAndAudit()
    {
        var configuration = await SeedConfigurationAsync(enabled: false);
        await _providerRegistry.UpsertAsync("git", "/plugins/git.slothandler.dll");

        await _capturedHandler!(ConfigurationCommand(configuration.Id), CancellationToken.None);

        await AssertPreFlightFailedAsync("disabled");
    }

    [Test]
    public async Task ConfigurationWithUnregisteredProvider_FailsPreFlightWithStatusEventAndAudit()
    {
        var configuration = await SeedConfigurationAsync(providerType: "unregistered-provider");

        await _capturedHandler!(ConfigurationCommand(configuration.Id), CancellationToken.None);

        await AssertPreFlightFailedAsync("unregistered-provider");
    }
}

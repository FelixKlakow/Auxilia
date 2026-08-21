using System.Text.Json;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Messaging;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Container re-adoption after a runner restart: the pure planner (records × containers →
/// adopt / collect-exit / fail-record / clean-kill), the token-registry restore, the per-request
/// container labels, and the service driving it all against a fake container host.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkflowReadoptionTests
{
    // --- Planner (pure) ---

    private static WorkflowInstanceRecord Record(
        Guid? id = null, string state = "Running", string? containerId = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            WorkflowType = "wf-type",
            State = state,
            ContainerId = containerId,
            CreatedUtc = DateTimeOffset.UtcNow
        };

    [Test]
    public void Planner_MatchesByContainerId_RunningAdopted_ExitedCollected()
    {
        var running = Record(containerId: "c-running");
        var exited = Record(containerId: "c-exited");
        var plan = ReadoptionPlanner.Plan(
            [running, exited],
            [new WorkflowContainerInfo("c-running", null, IsRunning: true),
             new WorkflowContainerInfo("c-exited", null, IsRunning: false)]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Adopt.Single().Record.Id, Is.EqualTo(running.Id));
            Assert.That(plan.CollectExit.Single().Record.Id, Is.EqualTo(exited.Id));
            Assert.That(plan.FailRecord, Is.Empty);
            Assert.That(plan.CleanKill, Is.Empty);
        });
    }

    [Test]
    public void Planner_FallsBackToTheInstanceIdLabel_WhenNoContainerIdWasPersisted()
    {
        var record = Record(containerId: null);
        var plan = ReadoptionPlanner.Plan(
            [record],
            [new WorkflowContainerInfo("c-labeled", record.Id, IsRunning: true)]);

        Assert.That(plan.Adopt.Single().Container.ContainerId, Is.EqualTo("c-labeled"));
    }

    [Test]
    public void Planner_RecordWithoutContainer_Fails_ContainerWithoutRecord_CleanKilled()
    {
        var orphanRecord = Record(containerId: "c-gone");
        var strayInstance = Guid.NewGuid();
        var plan = ReadoptionPlanner.Plan(
            [orphanRecord],
            [new WorkflowContainerInfo("c-stray", strayInstance, IsRunning: true)]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.FailRecord.Single().Id, Is.EqualTo(orphanRecord.Id));
            Assert.That(plan.CleanKill.Single(), Is.EqualTo(new CleanKillTarget("c-stray", strayInstance)));
        });
    }

    // --- Token registry restore ---

    [Test]
    public void TokenRegistry_Restore_KeepsTheCredentialValidating()
    {
        var registry = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System);
        var instanceId = Guid.NewGuid();

        registry.Restore(instanceId, "tok-123", "wf-type", registered: true);

        Assert.Multiple(() =>
        {
            Assert.That(registry.Validate(instanceId, "tok-123"), Is.True,
                "the restored token must keep JIT slot activations working");
            Assert.That(registry.IsRegistered(instanceId), Is.True);
            Assert.That(registry.TryBeginRegistration(instanceId, "tok-123"), Is.False,
                "a registered instance must not register a second time");
        });
    }

    [Test]
    public void TokenRegistry_Restore_Unregistered_GetsAFreshRegistrationWindow()
    {
        var registry = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System);
        var instanceId = Guid.NewGuid();

        registry.Restore(instanceId, "tok-456", "wf-type", registered: false);

        Assert.That(registry.TryBeginRegistration(instanceId, "tok-456"), Is.True,
            "a not-yet-registered instance may still complete its (single) registration");
    }

    // --- Container labels ---

    [Test]
    public void Launcher_LabelsEveryContainer_WithTheInstanceId()
    {
        var instanceId = Guid.NewGuid();
        var labels = DockerWorkflowLauncher.BuildContainerLabels(
            new WorkflowLaunchRequest("", new Dictionary<string, string>()) { InstanceId = instanceId });

        Assert.Multiple(() =>
        {
            Assert.That(labels[DockerWorkflowLauncher.WorkflowLabel], Is.EqualTo("1"));
            Assert.That(labels[DockerWorkflowLauncher.InstanceIdLabel], Is.EqualTo(instanceId.ToString("D")));
        });
    }

    // --- The service against a fake container host ---

    private sealed class FakeContainerHost : IWorkflowContainerHost
    {
        public List<WorkflowContainerInfo> Containers { get; } = [];
        public List<string> Removed { get; } = [];
        public List<string> Attached { get; } = [];

        /// <summary>When set, an attach immediately reports this exit (an already-dead container).</summary>
        public ContainerExit? ImmediateExit { get; set; }

        public Task<IReadOnlyList<WorkflowContainerInfo>> ListWorkflowContainersAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkflowContainerInfo>>(Containers);

        public void AttachExitWatcher(string containerId, Func<ContainerExit, Task> onExited)
        {
            Attached.Add(containerId);
            if (ImmediateExit is { } exit)
                onExited(exit).GetAwaiter().GetResult();
        }

        public Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
        {
            Removed.Add(containerId);
            return Task.CompletedTask;
        }
    }

    private IDataAccess<WorkflowInstanceRecord> _records = null!;
    private WorkflowInstanceRegistry _registry = null!;
    private WorkflowInstanceTokenRegistry _tokens = null!;
    private FakeContainerHost _host = null!;
    private List<WorkflowStatusEvent> _published = null!;
    private CoreRunnerInfo _instanceInfo = null!;
    private WorkflowSchemaStore _schemaStore = null!;
    private Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry _podControlRegistry = null!;
    private WorkflowReadoptionService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _records = new InMemoryDataAccess<WorkflowInstanceRecord>();
        _registry = new WorkflowInstanceRegistry(_records, TimeProvider.System);
        _tokens = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System);
        _host = new FakeContainerHost();
        _published = [];
        _instanceInfo = TestStores.NewInstanceInfo();

        var bus = new Mock<IMessageBusClient>();
        bus.Setup(b => b.PublishToTopicExchangeAsync(
                WorkflowStatusEvent.ExchangeName, It.IsAny<string>(),
                It.IsAny<WorkflowStatusEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorkflowStatusEvent, CancellationToken>((_, _, evt, _) => _published.Add(evt))
            .Returns(Task.CompletedTask);

        // Grace 0 so the collect-exit path resolves synchronously in tests.
        var dispatcherSettings = new WorkflowDispatcherSettings { ContainerExitGraceSeconds = 0 };
        var statusPublisher = TestStores.NewStatusPublisher(bus.Object);
        var dispatcher = new WorkflowDispatcher(
            bus.Object,
            Mock.Of<IWorkflowLauncher>(),
            Options.Create(new DockerWorkflowLauncherSettings()),
            Options.Create(dispatcherSettings),
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<Auxilia.Workflows.Crypto.IWorkflowPackageVerifier>(),
            new Mock<PendingWorkflowPackageStore>().Object,
            TestStores.NewSlotProviderRegistry(),
            _tokens,
            TestStores.NewPolicyEngine(),
            _registry,
            statusPublisher,
            TestStores.NewWorkflowSchemaStore(),
            TestStores.NewWorkflowPackageStore(),
            TestStores.NewArtifactStore(),
            new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance),
            TestStores.NewWorkspaceManager(),
            TestStores.NewHostPlatformProbe(),
            Mock.Of<IRepositoryAuthResolver>(),
            TestStores.NewAuditLog(),
            _instanceInfo,
            new NullSettingsProtector(),
            new FakePodHost(),
            new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry(),
            NullLogger<WorkflowDispatcher>.Instance);

        _schemaStore = TestStores.NewWorkflowSchemaStore();
        _podControlRegistry = new Auxilia.Core.Runner.Workflows.Pods.PodControlRegistry();
        _sut = new WorkflowReadoptionService(
            _host, _registry, _tokens, _schemaStore, _podControlRegistry,
            dispatcher, statusPublisher,
            TestStores.NewWorkspaceManager(), new NullSettingsProtector(),
            Options.Create(dispatcherSettings), _instanceInfo, TestStores.NewAuditLog(),
            new FakePodHost(),
            NullLogger<WorkflowReadoptionService>.Instance);
    }

    [TearDown]
    public void TearDown() => (_records as IDisposable)?.Dispose();

    private async Task<WorkflowInstanceRecord> SeedAsync(
        string state, string? containerId, string? token = "tok-1", string? podBaseImagesJson = null)
    {
        var commandId = Guid.NewGuid();
        var record = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "wf-type",
            State = state,
            CreatedUtc = DateTimeOffset.UtcNow,
            OwnerServiceId = Guid.NewGuid(), // the DEAD previous runner
            ContainerId = containerId,
            ProtectedInstanceToken = token,
            DispatchCommandJson = JsonSerializer.Serialize(new RunWorkflowCommand(
                commandId, "wf-type", "docker://wf:test",
                new Dictionary<string, string>(), ResolutionToken: "rt",
                PodBaseImagesJson: podBaseImagesJson))
        };
        await _records.SaveAsync(record);
        return record;
    }

    [Test]
    public async Task RunningContainer_IsReclaimed_TokenRestored_WatcherAttached()
    {
        var record = await SeedAsync("Running", "c-1");
        _host.Containers.Add(new WorkflowContainerInfo("c-1", record.Id, IsRunning: true));

        await _sut.RunAsync();

        var updated = await _records.ReadAsync(record.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updated!.OwnerServiceId, Is.EqualTo(_instanceInfo.ServiceId),
                "the run must be RE-CLAIMED under the fresh ServiceId");
            Assert.That(updated.State, Is.EqualTo("Running"), "the state is untouched — the run continues");
            Assert.That(_published.Single(e => e.WorkflowInstanceId == record.Id).OwnerServiceId,
                Is.EqualTo(_instanceInfo.ServiceId),
                "the re-claim rides a status event so the Core learns the new owner");
            Assert.That(_tokens.Validate(record.Id, "tok-1"), Is.True,
                "the restored token keeps the in-container SDK authenticating");
            Assert.That(_tokens.IsRegistered(record.Id), Is.True, "Running implies registered");
            Assert.That(_host.Attached, Is.EqualTo(new[] { "c-1" }),
                "the exit watcher must be re-attached");
            Assert.That(_host.Removed, Is.Empty);
        });
    }

    [Test]
    public async Task AdoptedPodControlledRun_GetsItsPodControlStateRebuilt()
    {
        const string pinned =
            "sim@sha256:3333333333333333333333333333333333333333333333333333333333333333";
        await _schemaStore.SetSchemaAsync("wf-type", new Auxilia.Workflows.WorkflowSchema("wf-type", [], [])
        {
            PodControl = new Auxilia.Workflows.Companions.PodControlDeclaration(3, "fleet")
            {
                PodVolumes = ["logs"]
            }
        });
        var record = await SeedAsync("Running", "c-pod",
            podBaseImagesJson: JsonSerializer.Serialize(
                new Dictionary<string, string> { ["sim-base"] = pinned }));
        _host.Containers.Add(new WorkflowContainerInfo("c-pod", record.Id, IsRunning: true));

        await _sut.RunAsync();

        var state = _podControlRegistry.Get(record.Id);
        Assert.Multiple(() =>
        {
            Assert.That(state, Is.Not.Null,
                "an adopted run must keep its runtime spawn capability across the restart");
            Assert.That(state!.MaxContainers, Is.EqualTo(3));
            Assert.That(state.BaseImages["sim-base"], Is.EqualTo(pinned),
                "the configuration-pinned base map survives via the persisted dispatch command");
            Assert.That(state.NetworkName, Is.EqualTo($"auxilia-pod-{record.Id:N}"),
                "the network name re-derives deterministically");
            Assert.That(state.VolumeNames["logs"], Is.EqualTo($"auxilia-pod-{record.Id:N}-logs"));
        });
    }

    [Test]
    public async Task AdoptedRunWithoutAPodEnvelope_RegistersNoPodControlState()
    {
        var record = await SeedAsync("Running", "c-plain");
        _host.Containers.Add(new WorkflowContainerInfo("c-plain", record.Id, IsRunning: true));

        await _sut.RunAsync();

        Assert.That(_podControlRegistry.Get(record.Id), Is.Null);
    }

    [Test]
    public async Task ExitedContainer_GetsItsRealExitCollected_RunFailsWithEvidence()
    {
        var record = await SeedAsync("Running", "c-2");
        _host.Containers.Add(new WorkflowContainerInfo("c-2", record.Id, IsRunning: false));
        _host.ImmediateExit = new ContainerExit(137, "oom killed");

        await _sut.RunAsync();

        var updated = await _records.ReadAsync(record.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updated!.State, Is.EqualTo("Failed"));
            Assert.That(updated.ErrorMessage, Does.Contain("code 137").And.Contain("oom killed"),
                "the REAL exit code + log tail must survive the restart, not a generic failure");
        });
    }

    [Test]
    public async Task RecordWithoutContainer_IsFailedVisibly()
    {
        var record = await SeedAsync("Queued", "c-vanished");

        await _sut.RunAsync();

        var updated = await _records.ReadAsync(record.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updated!.State, Is.EqualTo("Failed"));
            Assert.That(updated.ErrorMessage, Does.Contain("container no longer exists"));
            Assert.That(_published.Any(e =>
                e.WorkflowInstanceId == record.Id && e.State == "Failed"), Is.True);
        });
    }

    [Test]
    public async Task ContainerWithoutALiveRun_IsCleanKilled()
    {
        _host.Containers.Add(new WorkflowContainerInfo("c-stray", Guid.NewGuid(), IsRunning: true));

        await _sut.RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_host.Removed, Is.EqualTo(new[] { "c-stray" }));
            Assert.That(_host.Attached, Is.Empty);
            Assert.That(_published, Is.Empty, "no run identity exists to publish for");
        });
    }
}

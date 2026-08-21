using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Pods;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows.Companions;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Runtime pod control (run-pod design §"pod controller"): spawns are restricted to the
/// run's configuration-pinned base map, clamped to the signed envelope on the LIVE count,
/// volume mounts must reference declared pod volumes, and an unauthenticated request is
/// dropped without a response.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class PodControlHandlerTests
{
    private const string PinnedImage =
        "sim@sha256:4444444444444444444444444444444444444444444444444444444444444444";

    private Mock<IMessageBusClient> _mockBus = null!;
    private FakePodHost _podHost = null!;
    private PodControlRegistry _registry = null!;
    private WorkflowInstanceTokenRegistry _tokenRegistry = null!;
    private Func<PodControlRequest, CancellationToken, Task>? _capturedHandler;
    private readonly List<PodControlResponse> _responses = [];
    private PodControlHandler _sut = null!;
    private Guid _instanceId;
    private string _token = null!;

    [SetUp]
    public async Task SetUp()
    {
        _responses.Clear();
        _mockBus = new Mock<IMessageBusClient>();
        _mockBus
            .Setup(b => b.SubscribeAsync<PodControlRequest>(
                "workflow-pod-control",
                It.IsAny<Func<PodControlRequest, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<PodControlRequest, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        _mockBus
            .Setup(b => b.PublishAsync(
                It.IsAny<string>(), It.IsAny<PodControlResponse>(), It.IsAny<CancellationToken>()))
            .Callback<string, PodControlResponse, CancellationToken>((_, r, _) => _responses.Add(r))
            .Returns(Task.CompletedTask);

        _podHost = new FakePodHost();
        _registry = new PodControlRegistry();
        _tokenRegistry = new WorkflowInstanceTokenRegistry(
            Options.Create(new WorkflowDispatcherSettings()), TimeProvider.System);
        var issued = _tokenRegistry.Issue("pod-workflow");
        _instanceId = issued.WorkflowInstanceId;
        _token = issued.Token;
        _registry.Register(_instanceId, new PodControlState(
            MaxContainers: 2,
            BaseImages: new Dictionary<string, string> { ["sim-base"] = PinnedImage },
            NetworkName: $"auxilia-pod-{_instanceId:N}",
            VolumeNames: new Dictionary<string, string> { ["bin"] = "vol-bin" }));

        _sut = new PodControlHandler(
            _mockBus.Object, _podHost, _registry, _tokenRegistry,
            TestStores.NewAuditLog(),
            Options.Create(new WorkflowDispatcherSettings { RequireInstanceToken = true }),
            NullLogger<PodControlHandler>.Instance);
        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown() => await _sut.StopAsync();

    private Task SendAsync(string action, CompanionSpec spec, string? token = null)
        => _capturedHandler!(new PodControlRequest(
            _instanceId, action, JsonSerializer.Serialize(spec), Guid.NewGuid(),
            token ?? _token), CancellationToken.None);

    [Test]
    public async Task Spawn_FromConfiguredBase_StartsTheCompanionAndAnswersItsEndpoint()
    {
        await SendAsync(PodControlRequest.Spawn, new CompanionSpec("machine-07", "sim-base")
        {
            Command = ["/workspace/pod/bin/sim", "--type", "A"],
            Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 9000),
            VolumeMounts = new Dictionary<string, string> { ["bin"] = "/workspace/pod/bin" }
        });

        var spawned = _podHost.Spawned.Single();
        var result = JsonSerializer.Deserialize<SpawnedCompanion>(
            _responses.Single().ResultJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Multiple(() =>
        {
            Assert.That(_responses.Single().Success, Is.True);
            Assert.That(spawned.Companion.Image, Is.EqualTo(PinnedImage),
                "the base NAME resolves to the pinned catalog image — never a caller image");
            Assert.That(spawned.Companion.Command, Is.EqualTo(new[] { "/workspace/pod/bin/sim", "--type", "A" }));
            Assert.That(spawned.Companion.VolumeBinds, Is.EqualTo(new[] { "vol-bin:/workspace/pod/bin" }));
            Assert.That(spawned.NetworkName, Is.EqualTo($"auxilia-pod-{_instanceId:N}"));
            Assert.That(result!.Endpoint, Is.EqualTo("machine-07:9000"));
        });
    }

    [Test]
    public async Task Spawn_UnknownBase_IsRefused()
    {
        await SendAsync(PodControlRequest.Spawn, new CompanionSpec("m", "not-configured"));

        Assert.Multiple(() =>
        {
            Assert.That(_responses.Single().Success, Is.False);
            Assert.That(_responses.Single().ErrorMessage, Does.Contain("not among this run's configured"));
            Assert.That(_podHost.Spawned, Is.Empty);
        });
    }

    [Test]
    public async Task Spawn_BeyondTheEnvelope_IsRefused_AndAStopFreesTheSlot()
    {
        await SendAsync(PodControlRequest.Spawn, new CompanionSpec("m-1", "sim-base"));
        await SendAsync(PodControlRequest.Spawn, new CompanionSpec("m-2", "sim-base"));
        await SendAsync(PodControlRequest.Spawn, new CompanionSpec("m-3", "sim-base"));

        Assert.That(_responses[2].Success, Is.False,
            "the third spawn exceeds MaxContainers=2");
        Assert.That(_responses[2].ErrorMessage, Does.Contain("2 of 2"));

        await SendAsync(PodControlRequest.Stop, new CompanionSpec("m-2", ""));
        await SendAsync(PodControlRequest.Spawn, new CompanionSpec("m-4", "sim-base"));

        Assert.That(_responses[4].Success, Is.True,
            "the envelope clamps the LIVE count — a stopped companion frees its slot");
    }

    [Test]
    public async Task Spawn_UndeclaredVolumeMount_IsRefused()
    {
        await SendAsync(PodControlRequest.Spawn, new CompanionSpec("m", "sim-base")
        {
            VolumeMounts = new Dictionary<string, string> { ["ghost"] = "/data" }
        });

        Assert.That(_responses.Single().Success, Is.False);
        Assert.That(_responses.Single().ErrorMessage, Does.Contain("not a declared pod volume"));
    }

    [Test]
    public async Task Request_WithInvalidToken_IsDroppedWithoutResponse()
    {
        await SendAsync(PodControlRequest.Spawn, new CompanionSpec("m", "sim-base"), token: "wrong");

        Assert.Multiple(() =>
        {
            Assert.That(_responses, Is.Empty, "an unauthenticated caller learns nothing");
            Assert.That(_podHost.Spawned, Is.Empty);
        });
    }

    [Test]
    public async Task Request_ForRunWithoutState_IsRefused()
    {
        _registry.Consume(_instanceId);

        await SendAsync(PodControlRequest.Spawn, new CompanionSpec("m", "sim-base"));

        Assert.That(_responses.Single().ErrorMessage, Does.Contain("no pod-control state"));
    }

    [Test]
    public async Task Stop_UnknownCompanion_IsRefused()
    {
        await SendAsync(PodControlRequest.Stop, new CompanionSpec("ghost", ""));

        Assert.That(_responses.Single().Success, Is.False);
        Assert.That(_responses.Single().ErrorMessage, Does.Contain("does not exist"));
    }
}

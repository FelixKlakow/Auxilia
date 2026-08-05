using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.DispatchTimeout;

/// <summary>
/// The claim-timeout sweep (hardening wave 2026-08-04): a dispatch NOBODY ever claims leaves a
/// visible <c>Dispatched</c> record and is failed over as <c>dispatch-never-claimed</c> once the
/// claim window expires. The sweep only arms while a runner LOOKS alive (without one, dispatch
/// is rejected outright; with <c>AllowDispatchWithoutRunner</c> queueing is deliberate) — so the
/// fixture runs a real runner whose heartbeats flow but which consumes a DIFFERENT command
/// queue: the exact "runner alive, claim lost" failure the sweep exists for.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class DispatchTimeoutSystemTests
{
    private const string BootstrapApiKey = "aux-system-test-key-dispatch-timeout-0123";

    private INetwork _network = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private IContainer _coreApi = null!;
    private IContainer _runner = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await WorkflowDispatchEnvironment.BuildImageAsync(
            Failover.FailoverEnvironment.CoreApiImageName, "Source/Platform/Auxilia.Core.Api/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowDispatchEnvironment.RunnerImageName, "Source/Platform/Auxilia.Core.Runner/Dockerfile");

        _network = new NetworkBuilder()
            .WithName($"auxilia-dispatch-timeout-{Guid.NewGuid():N}".Substring(0, 30)).Build();
        await _network.CreateAsync();
        _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management")
            .WithUsername("guest").WithPassword("guest")
            .WithNetwork(_network).WithNetworkAliases("rabbitmq")
            .Build();
        await _rabbitMq.StartAsync();

        // Heartbeats flow; the command queue the Core dispatches onto is never consumed.
        _runner = new ContainerBuilder(WorkflowDispatchEnvironment.RunnerImageName)
            .WithNetwork(_network)
            .WithEnvironment("RabbitMq__Host", "rabbitmq")
            .WithEnvironment("RabbitMq__Port", "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("WorkflowDispatcher__CommandQueueName", "workflow.run-commands-never-consumed")
            .WithEnvironment("WorkflowDispatcher__HeartbeatIntervalSeconds", "2")
            .WithEnvironment("WorkflowLauncher__ReadoptContainersOnStart", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Runner heartbeat started"))
            .Build();

        _coreApi = new ContainerBuilder(Failover.FailoverEnvironment.CoreApiImageName)
            .WithNetwork(_network)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("RabbitMq__Host", "rabbitmq")
            .WithEnvironment("RabbitMq__Port", "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("PlatformData__Backend", "InMemory")
            .WithEnvironment("PlatformData__ProtectionKeyBase64", Convert.ToBase64String(new byte[32]))
            .WithEnvironment("CoreSecurity__BootstrapApiKey", BootstrapApiKey)
            .WithEnvironment("CoreApi__RunCommandQueue", "workflow.run-commands-dispatch-timeout")
            .WithEnvironment("CoreApi__DispatchClaimTimeoutSeconds", "5")
            .WithEnvironment("CoreApi__FailoverScanIntervalSeconds", "2")
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__WorkflowType", "sleeping-workflow")
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__PackageUri", "docker://never-runs:latest")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("FailoverMonitor started"))
            .Build();
        await Task.WhenAll(_runner.StartAsync(), _coreApi.StartAsync());

        _client = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_coreApi.GetMappedPublicPort(8080)}")
        };
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BootstrapApiKey);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _client?.Dispose();
        if (_coreApi is not null) await _coreApi.DisposeAsync();
        if (_runner is not null) await _runner.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _network.DisposeAsync();
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task UnclaimedDispatch_IsVisibleAsDispatched_ThenSweptAsNeverClaimed(
        CancellationToken cancellationToken)
    {
        var response = await _client.PostAsJsonAsync("/api/runs",
            new RunRequest("sleeping-workflow", new Dictionary<string, string>()), cancellationToken);
        response.EnsureSuccessStatusCode();
        var accepted = await response.Content.ReadFromJsonAsync<RunAccepted>(cancellationToken);

        // The run exists from the moment it is accepted — visible as Dispatched, never traceless.
        var initial = await _client.GetFromJsonAsync<RunStatus>(
            $"/api/runs/{accepted!.RunId}", cancellationToken);
        Assert.That(initial!.State, Is.EqualTo(RunStates.Dispatched));

        // The claim-timeout sweep fails it over once the window (5s here) expires.
        RunStatus? swept = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            swept = await _client.GetFromJsonAsync<RunStatus>(
                $"/api/runs/{accepted.RunId}", cancellationToken);
            if (RunStates.IsTerminal(swept!.State))
                break;
            await Task.Delay(1000, cancellationToken);
        }

        Assert.Multiple(() =>
        {
            Assert.That(swept!.State, Is.EqualTo("Failed"),
                "a dispatch nobody claims must fail visibly, never queue forever silently");
            Assert.That(swept.Error, Does.Contain("dispatch-never-claimed"));
        });
    }
}

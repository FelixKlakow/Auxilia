using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Auxilia.SystemTestSuite.Egress;

/// <summary>
/// The run topology the runner builds, on a real Docker daemon: a client on the run's
/// <c>--internal</c> network reaches an allowed destination only through the egress gateway,
/// is refused (and the refusal logged) for anything else, and has no direct route out at all.
/// The gateway runs hardened exactly like <c>DockerEgressHost</c> starts it (unprivileged, no
/// capabilities, read-only) — its declared TCP relays still listen on privileged ports.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class EgressIsolationSystemTests
{
    private const string TargetImage = "nginx:1.27-alpine";
    private const string ClientImage = "curlimages/curl:8.10.1";

    private INetwork _outbound = null!;
    private INetwork _run = null!;
    private IContainer _allowed = null!;
    private IContainer _denied = null!;
    private IContainer _gateway = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await TestImages.BuildEgressGatewayAsync();
        _outbound = new NetworkBuilder().WithName($"auxilia-egress-out-{Guid.NewGuid():N}"[..30]).Build();
        _run = new NetworkBuilder().WithName($"auxilia-egress-run-{Guid.NewGuid():N}"[..30])
            .WithCreateParameterModifier(p => p.Internal = true).Build();
        await _outbound.CreateAsync();
        await _run.CreateAsync();

        _allowed = new ContainerBuilder(TargetImage).WithNetwork(_outbound).WithNetworkAliases("allowed-target").Build();
        _denied = new ContainerBuilder(TargetImage).WithNetwork(_outbound).WithNetworkAliases("denied-target").Build();
        await Task.WhenAll(_allowed.StartAsync(), _denied.StartAsync());

        _gateway = new ContainerBuilder(TestImages.EgressGatewayImageName)
            .WithNetwork(_run).WithNetworkAliases("egress")
            .WithEnvironment("EGRESS_ALLOW", "allowed-target:80")
            // Declared raw-TCP endpoints: one on a PRIVILEGED port, one rebinding onto loopback.
            .WithEnvironment("EGRESS_FORWARDS", "80=allowed-target:80/tcp,8081=localhost:3128/tcp")
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.CapDrop = ["ALL"];
                p.HostConfig.SecurityOpt = ["no-new-privileges:true"];
                p.HostConfig.ReadonlyRootfs = true;
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("\"event\":\"listening\""))
            .Build();
        await _gateway.StartAsync();
        // The outbound leg — exactly like DockerEgressHost: only the gateway joins it.
        await ConnectAsync(_outbound.Name, _gateway.Id);
    }

    private static async Task ConnectAsync(string network, string containerId)
    {
        using var docker = new Docker.DotNet.DockerClientConfiguration().CreateClient();
        await docker.Networks.ConnectNetworkAsync(network,
            new Docker.DotNet.Models.NetworkConnectParameters { Container = containerId });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_gateway is not null) await _gateway.DisposeAsync();
        if (_allowed is not null) await _allowed.DisposeAsync();
        if (_denied is not null) await _denied.DisposeAsync();
        if (_run is not null) await _run.DisposeAsync();
        if (_outbound is not null) await _outbound.DisposeAsync();
    }

    private async Task<string> GatewayAddressOnTheRunNetworkAsync()
    {
        using var docker = new Docker.DotNet.DockerClientConfiguration().CreateClient();
        var inspected = await docker.Containers.InspectContainerAsync(_gateway.Id);
        return inspected.NetworkSettings.Networks[_run.Name].IPAddress;
    }

    private Task<string> CurlFromTheRunNetworkAsync(params string[] arguments)
        => CurlFromTheRunNetworkAsync(null, arguments);

    private async Task<string> CurlFromTheRunNetworkAsync((string Host, string Address)? hostEntry, params string[] arguments)
    {
        var builder = new ContainerBuilder(ClientImage).WithNetwork(_run);
        if (hostEntry is { } entry)
            builder = builder.WithExtraHost(entry.Host, entry.Address);
        await using var client = builder
            .WithEntrypoint("curl")
            .WithCommand(["-s", "-o", "/dev/null", "-w", "%{http_code}", "--max-time", "8", .. arguments])
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();
        await client.StartAsync();
        await client.GetExitCodeAsync();
        var (stdout, _) = await client.GetLogsAsync(timestampsEnabled: false);
        // The status code is the last token whatever framing the log stream adds.
        return stdout.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
    }

    [Test]
    public async Task AnAllowedDestination_IsReachable_ThroughTheGateway()
        => Assert.That(await CurlFromTheRunNetworkAsync("-x", "http://egress:3128", "http://allowed-target/"),
            Is.EqualTo("200"));

    [Test]
    public async Task AnUnlistedDestination_IsRefused_AndTheRefusalIsLogged()
    {
        Assert.That(await CurlFromTheRunNetworkAsync("-x", "http://egress:3128", "http://denied-target/"),
            Is.EqualTo("403"));

        var (log, _) = await _gateway.GetLogsAsync();
        Assert.That(log.Split('\n'), Has.Some.Contains("\"decision\":\"deny\"").And.Contains("denied-target"));
    }

    [Test]
    public async Task ThereIsNoDirectRouteOut_ByNameOrAddress()
    {
        Assert.That(await CurlFromTheRunNetworkAsync("http://allowed-target/"), Is.Not.EqualTo("200"),
            "the run network resolves and routes nothing outside it");
        Assert.That(await CurlFromTheRunNetworkAsync($"http://{_allowed.IpAddress}/"), Is.Not.EqualTo("200"));
    }

    [Test]
    public async Task ADeclaredTcpEndpoint_IsRelayedUnderItsOwnName_OnAPrivilegedPort()
    {
        // Exactly the workload's view: the endpoint's name resolves to its gateway (host file),
        // the client speaks its protocol directly — no proxy involved.
        var gateway = await GatewayAddressOnTheRunNetworkAsync();
        Assert.That(await CurlFromTheRunNetworkAsync(("allowed-target", gateway), "http://allowed-target/"),
            Is.EqualTo("200"));
    }

    [Test]
    public async Task ATcpRelayOntoLoopback_IsRefused_AndLogged()
    {
        Assert.That(await CurlFromTheRunNetworkAsync("http://egress:8081/"), Is.Not.EqualTo("200"));

        var (log, _) = await _gateway.GetLogsAsync();
        Assert.That(log.Split('\n'), Has.Some.Contains("\"decision\":\"deny\"").And.Contains("\"port\":3128"));
    }

    [Test]
    public async Task AnAllowedTlsDestination_IsTunnelledWithConnect_AndAnUnlistedOneIsNot()
    {
        // CONNECT to a plain-HTTP port is enough to prove the tunnel decision both ways.
        Assert.That(await CurlFromTheRunNetworkAsync("-p", "-x", "http://egress:3128", "http://allowed-target/"),
            Is.EqualTo("200"));
        Assert.That(await CurlFromTheRunNetworkAsync("-p", "-x", "http://egress:3128", "http://denied-target/"),
            Is.Not.EqualTo("200"));
    }
}

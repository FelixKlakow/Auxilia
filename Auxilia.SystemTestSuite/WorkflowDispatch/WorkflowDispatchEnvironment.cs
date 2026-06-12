using System.Diagnostics;
using Auxilia.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.WorkflowDispatch;

/// <summary>
/// Shared environment for workflow dispatch system tests.
///
/// Topology:
/// <list type="bullet">
///   <item>RabbitMQ — message broker (alias <c>rabbitmq</c>).</item>
///   <item>SteeringInstance — control plane; has the Docker socket bind-mounted so it can
///         launch workflow containers via the Docker API (<c>DockerWorkflowLauncher</c>).</item>
///   <item>Dummy-workflows image — pre-built before the test run; launched on demand by the
///         SteeringInstance when a <c>RunWorkflowCommand</c> is received.</item>
/// </list>
///
/// MongoDB is intentionally omitted — it is not required for the dispatch smoke test.
/// </summary>
[SetUpFixture]
public class WorkflowDispatchEnvironment
{
    internal const string DummyWorkflowsImageName = "auxilia-dummy-workflows:system-test";
    internal const string SteeringImageName        = "auxilia-steeringinstance:system-test";
    private  const string RabbitMqAlias            = "rabbitmq";
    private  const string RabbitMqImage            = "rabbitmq:3.13-management";

    // Generated once per test session — used as the Docker network name so the
    // SteeringInstance can attach workflow containers to the same network.
    private static readonly string NetworkName =
        $"auxilia-dispatch-{Guid.NewGuid():N}".Substring(0, 30); // max 30 chars for Docker network names

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private IContainer _steeringInstance = null!;
    private INetwork   _network          = null!;
    private RabbitMqContainer _rabbitMq  = null!;

    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static string            RabbitMqHost     { get; private set; } = null!;
    public static int               RabbitMqPort     { get; private set; }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Build the SteeringInstance and dummy-workflows images in parallel from source.
        await Task.WhenAll(
            BuildImageAsync(SteeringImageName,     "Source/Auxilia.SteeringInstance/Dockerfile"),
            BuildImageAsync(DummyWorkflowsImageName, "Auxilia.Workflows.Testing/Dockerfile"));

        // Named network — name is passed to the SteeringInstance so it can attach
        // workflow containers to the same network.
        _network = new NetworkBuilder()
            .WithName(NetworkName)
            .Build();
        await _network.CreateAsync();

        // RabbitMQ
        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername("guest")
            .WithPassword("guest")
            .WithNetwork(_network)
            .WithNetworkAliases(RabbitMqAlias)
            .Build();
        await _rabbitMq.StartAsync();

        RabbitMqHost = _rabbitMq.Hostname;
        RabbitMqPort = _rabbitMq.GetMappedPublicPort(5672);

        // SteeringInstance — bind-mount the Docker socket so DockerWorkflowLauncher
        // can call the Docker API from inside the container.
        const string dockerSocket = "/var/run/docker.sock";

        _steeringInstance = new ContainerBuilder(SteeringImageName)
            .WithNetwork(_network)
            .WithBindMount(dockerSocket, dockerSocket)
            // Note: on Docker Desktop (Windows/Mac) the socket is world-accessible (mode 777).
            // On Linux CI the socket may require root; if so, override the user in the pipeline
            // via a Testcontainers future API or by adjusting socket group permissions.
            // RabbitMQ — SteeringInstance connects on the Docker-internal address.
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            // WorkflowLauncher — use Docker API, attach containers to the test network.
            .WithEnvironment("WorkflowLauncher__NetworkName",    NetworkName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost",   RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort",   "5672")
            .WithEnvironment("WorkflowLauncher__RabbitMqUserName", "guest")
            .WithEnvironment("WorkflowLauncher__RabbitMqPassword", "guest")
            // DockerSocketPath default (unix:///var/run/docker.sock) is correct on Linux.
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("WorkflowDispatcher started"))
            .Build();
        await _steeringInstance.StartAsync();

        // Test-side message bus — connects to the host-mapped RabbitMQ port.
        MessageBusClient = await RabbitMqClient.CreateAsync(RabbitMqHost, RabbitMqPort);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        await _steeringInstance.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _network.DisposeAsync();
    }

    internal static async Task BuildImageAsync(string tag, string dockerfilePath)
    {
        // Local opt-out: when the runner has already built all system-test images from
        // current source (docker builds spawned from the test process can hang on a wedged
        // Docker Desktop daemon), it signals that via the env var or a marker file in the
        // repo root. CI leaves both unset so images are always rebuilt from local source.
        if (System.Environment.GetEnvironmentVariable("AUXILIA_PREBUILT_IMAGES") == "1" ||
            File.Exists(Path.Combine(RepoRoot, ".prebuilt-images")))
        {
            await Console.Out.WriteLineAsync($"Prebuilt-images opt-out active — skipping docker build for {tag}.");
            return;
        }

        // The Docker Desktop daemon occasionally wedges under suite load and a build then
        // hangs forever at ~0 CPU. Bound each attempt and retry once instead of hanging.
        try
        {
            await BuildImageOnceAsync(tag, dockerfilePath, TimeSpan.FromMinutes(8));
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync(
                $"docker build for {tag} timed out — retrying once (daemon may have been wedged).");
            await BuildImageOnceAsync(tag, dockerfilePath, TimeSpan.FromMinutes(8));
        }
    }

    private static async Task BuildImageOnceAsync(string tag, string dockerfilePath, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("docker",
            $"build -t {tag} -f {dockerfilePath} .")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute  = false
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start docker build.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"docker build for {tag} exceeded {timeout.TotalMinutes:0} minutes.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"docker build failed for {tag} (exit {process.ExitCode}):\n{stdout}\n{stderr}");
    }
}











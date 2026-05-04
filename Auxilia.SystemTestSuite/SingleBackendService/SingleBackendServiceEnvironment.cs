using System.Diagnostics;
using Auxilia.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.SingleBackend;

/// <summary>
///     Shared environment for BackendService system tests.
///     Spins up RabbitMQ + one BackendService container once per test run in this namespace.
/// </summary>
[SetUpFixture]
public class SingleBackendServiceEnvironment
{
    internal const string BackendImageName = "auxilia-backendservice:system-test";
    private const string RabbitMqAlias = "rabbitmq";
    private const string RabbitMqImage = "rabbitmq:3.13-management";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private IContainer _backendService = null!;
    private INetwork _network = null!;
    private RabbitMqContainer _rabbitMq = null!;

    public static string RabbitMqHost { get; private set; } = null!;
    public static int RabbitMqPort { get; private set; }
    public static string RabbitMqUser { get; } = "guest";
    public static string RabbitMqPassword { get; } = "guest";
    public static IMessageBusClient MessageBusClient { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await BuildDockerImageAsync();

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername("guest")
            .WithPassword("guest")
            .WithNetwork(_network)
            .WithNetworkAliases(RabbitMqAlias)
            .Build();
        await _rabbitMq.StartAsync();

        RabbitMqHost = _rabbitMq.Hostname;
        RabbitMqPort = _rabbitMq.GetMappedPublicPort(5672);

        _backendService = new ContainerBuilder(BackendImageName)
            .WithNetwork(_network)
            .WithEnvironment("RabbitMq__Host", RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port", "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on:"))
            .Build();
        await _backendService.StartAsync();

        MessageBusClient = await RabbitMqClient.CreateAsync(
            RabbitMqHost,
            RabbitMqPort,
            RabbitMqUser,
            RabbitMqPassword);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        if (_backendService is not null)
        {
            await _backendService.StopAsync();
            await _backendService.DisposeAsync();
        }

        if (_rabbitMq is not null)
        {
            await _rabbitMq.StopAsync();
            await _rabbitMq.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DeleteAsync();
            await _network.DisposeAsync();
        }
    }

    internal static async Task BuildDockerImageAsync()
    {
        var psi = new ProcessStartInfo("docker",
            $"build -t {BackendImageName} -f Source/Auxilia.BackendService/Dockerfile .")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start docker build process.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"docker build failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");
    }
}
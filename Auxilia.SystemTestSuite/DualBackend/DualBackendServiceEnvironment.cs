using Auxilia.Messaging;
using Auxilia.SystemTestSuite.SingleBackend;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.DualBackend;

/// <summary>
///     Shared environment for dual-BackendService system tests.
///     Spins up RabbitMQ + two BackendService containers (Alpha and Beta) once per test run
///     in this namespace. Each service is configured to listen on its own queue.
/// </summary>
[SetUpFixture]
public class DualBackendServiceEnvironment
{
    private const string RabbitMqAlias = "rabbitmq";
    private const string RabbitMqImage = "rabbitmq:3.13-management";

    /// <summary>Queue name for the Alpha backend service instance.</summary>
    public const string AlphaQueueName = "backend-service-alpha";

    /// <summary>Queue name for the Beta backend service instance.</summary>
    public const string BetaQueueName = "backend-service-beta";

    private IContainer _backendServiceAlpha = null!;
    private IContainer _backendServiceBeta = null!;
    private INetwork _network = null!;
    private RabbitMqContainer _rabbitMq = null!;

    public static IMessageBusClient MessageBusClient { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Reuse the same Docker image built for the single-backend environment
        await SingleBackendServiceEnvironment.BuildDockerImageAsync();

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername("guest")
            .WithPassword("guest")
            .WithNetwork(_network)
            .WithNetworkAliases(RabbitMqAlias)
            .Build();
        await _rabbitMq.StartAsync();

        // Start both backend service instances concurrently
        _backendServiceAlpha = BuildBackendContainer(AlphaQueueName);
        _backendServiceBeta = BuildBackendContainer(BetaQueueName);
        await Task.WhenAll(
            _backendServiceAlpha.StartAsync(),
            _backendServiceBeta.StartAsync());

        var host = _rabbitMq.Hostname;
        var port = _rabbitMq.GetMappedPublicPort(5672);

        MessageBusClient = await RabbitMqClient.CreateAsync(host, port, "guest", "guest");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();

        if (_backendServiceAlpha is not null)
        {
            await _backendServiceAlpha.StopAsync();
            await _backendServiceAlpha.DisposeAsync();
        }

        if (_backendServiceBeta is not null)
        {
            await _backendServiceBeta.StopAsync();
            await _backendServiceBeta.DisposeAsync();
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

    private IContainer BuildBackendContainer(string queueName) =>
        new ContainerBuilder(SingleBackendServiceEnvironment.BackendImageName)
            .WithNetwork(_network)
            .WithEnvironment("RabbitMq__Host", RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port", "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("BackendService__QueueName", queueName)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on:"))
            .Build();
}

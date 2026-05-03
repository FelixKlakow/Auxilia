using Auxilia.Messaging;
using Auxilia.SystemTestSuite.SingleBackend;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.DualBackend;

/// <summary>
///     Shared environment for dual-BackendService system tests.
///     Spins up RabbitMQ + two BackendService containers (Alpha and Beta) once per test run
///     in this namespace. Each service is configured to listen on its own queue.
///
///     Also provisions:
///     <list type="bullet">
///         <item>An OpenTelemetry Collector (OTLP gRPC receiver, port 4317) that the backend containers
///               push traces/metrics to.</item>
///         <item>A Prometheus metrics scrape endpoint on each backend container (port 8080).</item>
///     </list>
/// </summary>
[SetUpFixture]
public class DualBackendServiceEnvironment
{
    private const string RabbitMqAlias = "rabbitmq";
    private const string RabbitMqImage = "rabbitmq:3.13-management";
    private const string OtelCollectorAlias = "otel-collector";
    private const string OtelCollectorImage = "otel/opentelemetry-collector-contrib:0.120.0";
    private const int BackendHttpPort = 8080;
    private const int OtelGrpcPort = 4317;
    private const int CollectorPrometheusPort = 9090;  // 8888 is reserved for collector self-telemetry

    /// <summary>Queue name for the Alpha backend service instance.</summary>
    public const string AlphaQueueName = "backend-service-alpha";

    /// <summary>Queue name for the Beta backend service instance.</summary>
    public const string BetaQueueName = "backend-service-beta";

    private IContainer _backendServiceAlpha = null!;
    private IContainer _backendServiceBeta = null!;
    private IContainer _otelCollector = null!;
    private INetwork _network = null!;
    private RabbitMqContainer _rabbitMq = null!;

    public static IMessageBusClient MessageBusClient { get; private set; } = null!;

    /// <summary>Host-mapped HTTP port for the Alpha backend container (Prometheus /metrics).</summary>
    public static int AlphaHttpPort { get; private set; }

    /// <summary>Host-mapped HTTP port for the Beta backend container (Prometheus /metrics).</summary>
    public static int BetaHttpPort { get; private set; }

    /// <summary>Host-mapped Prometheus scrape port on the OTLP collector container.</summary>
    public static int OtelCollectorPrometheusPort { get; private set; }

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

        // Start the OTel Collector before the backends so they can connect on first try
        _otelCollector = await BuildOtelCollectorAsync();

        // Start both backend service instances concurrently
        _backendServiceAlpha = BuildBackendContainer(AlphaQueueName);
        _backendServiceBeta = BuildBackendContainer(BetaQueueName);
        await Task.WhenAll(
            _backendServiceAlpha.StartAsync(),
            _backendServiceBeta.StartAsync());

        AlphaHttpPort = _backendServiceAlpha.GetMappedPublicPort(BackendHttpPort);
        BetaHttpPort = _backendServiceBeta.GetMappedPublicPort(BackendHttpPort);

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

        if (_otelCollector is not null)
        {
            await _otelCollector.StopAsync();
            await _otelCollector.DisposeAsync();
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
            .WithEnvironment("Otlp__Endpoint", $"http://{OtelCollectorAlias}:{OtelGrpcPort}")
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", BackendHttpPort.ToString())
            .WithPortBinding(BackendHttpPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on:"))
            .Build();

    private async Task<IContainer> BuildOtelCollectorAsync()
    {
        // Write the collector config to a temp file and bind-mount it
        var configPath = Path.Combine(Path.GetTempPath(), $"otel-collector-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(configPath, OtelCollectorConfig);

        var container = new ContainerBuilder(OtelCollectorImage)
            .WithNetwork(_network)
            .WithNetworkAliases(OtelCollectorAlias)
            .WithBindMount(configPath, "/etc/otelcol-contrib/config.yaml", AccessMode.ReadOnly)
            .WithPortBinding(OtelGrpcPort, true)
            .WithPortBinding(CollectorPrometheusPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(CollectorPrometheusPort).ForPath("/metrics")))
            .Build();

        await container.StartAsync();
        OtelCollectorPrometheusPort = container.GetMappedPublicPort(CollectorPrometheusPort);
        return container;
    }

    private const string OtelCollectorConfig = """
        receivers:
          otlp:
            protocols:
              grpc:
                endpoint: 0.0.0.0:4317

        processors:
          batch:
            timeout: 1s

        exporters:
          debug:
            verbosity: detailed
          prometheus:
            endpoint: "0.0.0.0:9090"

        service:
          pipelines:
            traces:
              receivers: [otlp]
              processors: [batch]
              exporters: [debug]
            metrics:
              receivers: [otlp]
              processors: [batch]
              exporters: [debug, prometheus]
        """;
}

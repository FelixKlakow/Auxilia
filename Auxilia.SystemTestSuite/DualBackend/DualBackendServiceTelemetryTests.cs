using Auxilia.Messaging;
using Auxilia.Messaging.Messages;

namespace Auxilia.SystemTestSuite.DualBackend;

/// <summary>
///     System tests that verify OpenTelemetry tracing and Prometheus metrics are flowing correctly
///     in the dual-backend environment.
/// </summary>
[TestFixture]
[Category("System")]
public class DualBackendServiceTelemetryTests
{
    private static IMessageBusClient Bus => DualBackendServiceEnvironment.MessageBusClient;

    /// <summary>
    ///     Verifies that the Alpha backend's Prometheus /metrics endpoint is reachable and contains
    ///     the identification-request counter metric after a request has been handled.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task AfterHandlingRequest_AlphaBackendPrometheusEndpointContainsIdentificationMetric(
        CancellationToken cancellationToken)
    {
        // Arrange – send a request and wait for the response
        await SendAndAwaitResponseAsync(
            DualBackendServiceEnvironment.AlphaQueueName,
            cancellationToken);

        // Allow a brief moment for the Prometheus exporter to update
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        // Act – scrape /metrics directly from the Alpha container
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{DualBackendServiceEnvironment.AlphaHttpPort}")
        };
        var response = await http.GetStringAsync("/metrics", cancellationToken);

        // Assert
        Assert.That(response, Does.Contain("identification_requests_received"),
            "Alpha backend /metrics should contain the identification_requests_received metric");
    }

    /// <summary>
    ///     Verifies that the Beta backend's Prometheus /metrics endpoint contains the metric.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task AfterHandlingRequest_BetaBackendPrometheusEndpointContainsIdentificationMetric(
        CancellationToken cancellationToken)
    {
        // Arrange
        await SendAndAwaitResponseAsync(
            DualBackendServiceEnvironment.BetaQueueName,
            cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        // Act
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{DualBackendServiceEnvironment.BetaHttpPort}")
        };
        var response = await http.GetStringAsync("/metrics", cancellationToken);

        // Assert
        Assert.That(response, Does.Contain("identification_requests_received"),
            "Beta backend /metrics should contain the identification_requests_received metric");
    }

    /// <summary>
    ///     Verifies that the OTel Collector's Prometheus endpoint receives metrics exported
    ///     from both backend services via OTLP.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task AfterHandlingRequests_OtelCollectorPrometheusEndpointContainsMetricsFromBothBackends(
        CancellationToken cancellationToken)
    {
        // Arrange – trigger one identification request on each backend
        await Task.WhenAll(
            SendAndAwaitResponseAsync(DualBackendServiceEnvironment.AlphaQueueName, cancellationToken),
            SendAndAwaitResponseAsync(DualBackendServiceEnvironment.BetaQueueName, cancellationToken));

        // OTLP metrics use a periodic exporter (configured at 5 s). Allow the first export
        // cycle to complete with a generous buffer.
        await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);

        // Act – poll the OTel Collector's Prometheus endpoint until the metric appears
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{DualBackendServiceEnvironment.OtelCollectorPrometheusPort}")
        };

        var scraped = false;
        string? metricsBody = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !scraped)
        {
            try
            {
                metricsBody = await http.GetStringAsync("/metrics", cancellationToken);
                if (metricsBody.Contains("identification_requests_received"))
                    scraped = true;
                else
                    await Task.Delay(3000, cancellationToken);
            }
            catch
            {
                await Task.Delay(3000, cancellationToken);
            }
        }

        // Assert
        Assert.That(scraped, Is.True,
            "OTel Collector Prometheus endpoint should contain 'identification_requests_received' " +
            $"from the backends. Last body snippet: " +
            $"{metricsBody?.Substring(0, Math.Min(500, metricsBody?.Length ?? 0))}");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task SendAndAwaitResponseAsync(string queueName, CancellationToken cancellationToken)
    {
        var responseTopic = $"telemetry-test-{queueName}-{Guid.NewGuid():N}";
        await Bus.DeclareQueueAsync(responseTopic, cancellationToken);

        var tcs = new TaskCompletionSource<bool>();
        await using var sub = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            responseTopic,
            (_, _) => { tcs.TrySetResult(true); return Task.CompletedTask; },
            cancellationToken);

        await Bus.PublishAsync(queueName,
            new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), responseTopic),
            cancellationToken);

        var timeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        var completed = await Task.WhenAny(tcs.Task, timeout);
        if (completed == timeout)
            Assert.Fail($"No response received from queue '{queueName}' within 30 seconds");
    }
}



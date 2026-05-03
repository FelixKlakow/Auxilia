using Auxilia.Messaging;
using Auxilia.Messaging.Messages;

namespace Auxilia.SystemTestSuite.SingleBackend;

/// <summary>
///     System tests for the single-BackendService environment.
///     The environment (RabbitMQ + BackendService containers) is shared across all tests in this class
///     via <see cref="SingleBackendServiceEnvironment" />.
/// </summary>
[TestFixture]
[Category("System")]
public class BackendServiceSystemTests
{
    private IMessageBusClient Bus => SingleBackendServiceEnvironment.MessageBusClient;

    [Test]
    [CancelAfter(30_000)]
    public async Task WhenEnvironmentStarted_BackendServiceQueueHasBeenCreated(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Exception? lastException = null;
        while (DateTime.UtcNow < deadline)
            try
            {
                await Bus.DeclareQueueAsync("backend-service", cancellationToken);
                // DeclareQueueAsync succeeded — queue exists. Return normally to pass the test.
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                await Task.Delay(500, cancellationToken);
            }

        Assert.Fail($"Queue 'backend-service' was not found within 20 seconds. Last error: {lastException?.Message}");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task WhenIdentificationRequestMessageSent_ServiceRespondsWithIdentificationResponseMessage(
        CancellationToken cancellationToken)
    {
        const string responseTopic = "system-test-identification-response";
        await Bus.DeclareQueueAsync(responseTopic, cancellationToken);

        var tcs = new TaskCompletionSource<IdentificationResponseMessage>();
        await using var subscription = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            responseTopic,
            (msg, _) =>
            {
                tcs.TrySetResult(msg);
                return Task.CompletedTask;
            },
            cancellationToken);

        var request = new IdentificationRequestMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            responseTopic);

        await Bus.PublishAsync("backend-service", request, cancellationToken);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(20), cancellationToken));
        Assert.That(completed, Is.EqualTo(tcs.Task), "No identification response received within 20 seconds.");

        var received = tcs.Task.Result;
        Assert.That(received.ServicePurpose, Is.EqualTo("AuxiliaBackendService"));
        Assert.That(received.ServiceId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(received.Version, Is.Not.Null.And.Not.Empty);
        Assert.That(received.StartupTimeUtc, Is.LessThanOrEqualTo(DateTime.UtcNow));
    }
}
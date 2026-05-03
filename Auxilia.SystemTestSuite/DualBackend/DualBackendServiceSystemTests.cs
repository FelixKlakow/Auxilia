using Auxilia.Messaging;
using Auxilia.Messaging.Messages;

namespace Auxilia.SystemTestSuite.DualBackend;

/// <summary>
///     System tests for the dual-BackendService environment.
///     The environment (RabbitMQ + Alpha + Beta containers) is shared across all tests in this class
///     via <see cref="DualBackendServiceEnvironment" />.
/// </summary>
[TestFixture]
[Category("System")]
public class DualBackendServiceSystemTests
{
    private static IMessageBusClient Bus => DualBackendServiceEnvironment.MessageBusClient;

    [Test]
    [CancelAfter(30_000)]
    public async Task WhenEnvironmentStarted_BothServiceQueuesHaveBeenCreated(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        foreach (var queueName in new[] { DualBackendServiceEnvironment.AlphaQueueName, DualBackendServiceEnvironment.BetaQueueName })
        {
            Exception? last = null;
            var declared = false;
            while (DateTime.UtcNow < deadline)
                try
                {
                    await Bus.DeclareQueueAsync(queueName, cancellationToken);
                    declared = true;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    await Task.Delay(500, cancellationToken);
                }

            Assert.That(declared, Is.True,
                $"Queue '{queueName}' was not found within 20 seconds. Last error: {last?.Message}");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task TwoServiceInstances_HaveDistinctServiceIds(CancellationToken cancellationToken)
    {
        var alphaResponseTopic = $"system-test-id-check-alpha-{Guid.NewGuid():N}";
        var betaResponseTopic = $"system-test-id-check-beta-{Guid.NewGuid():N}";

        await Bus.DeclareQueueAsync(alphaResponseTopic, cancellationToken);
        await Bus.DeclareQueueAsync(betaResponseTopic, cancellationToken);

        var alphaTcs = new TaskCompletionSource<IdentificationResponseMessage>();
        var betaTcs = new TaskCompletionSource<IdentificationResponseMessage>();

        await using var alphaSub = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            alphaResponseTopic,
            (msg, _) => { alphaTcs.TrySetResult(msg); return Task.CompletedTask; },
            cancellationToken);

        await using var betaSub = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            betaResponseTopic,
            (msg, _) => { betaTcs.TrySetResult(msg); return Task.CompletedTask; },
            cancellationToken);

        await Bus.PublishAsync(DualBackendServiceEnvironment.AlphaQueueName,
            new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), alphaResponseTopic),
            cancellationToken);

        await Bus.PublishAsync(DualBackendServiceEnvironment.BetaQueueName,
            new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), betaResponseTopic),
            cancellationToken);

        var timeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        var bothDone = Task.WhenAll(alphaTcs.Task, betaTcs.Task);

        Assert.That(await Task.WhenAny(bothDone, timeout), Is.EqualTo(bothDone),
            "Did not receive identification responses from both services within 30 seconds.");

        var alphaResponse = alphaTcs.Task.Result;
        var betaResponse = betaTcs.Task.Result;

        Assert.That(alphaResponse.ServiceId, Is.Not.EqualTo(Guid.Empty), "Alpha ServiceId should not be empty.");
        Assert.That(betaResponse.ServiceId, Is.Not.EqualTo(Guid.Empty), "Beta ServiceId should not be empty.");
        Assert.That(alphaResponse.ServiceId, Is.Not.EqualTo(betaResponse.ServiceId),
            "Alpha and Beta should have distinct ServiceIds — they are separate service instances.");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task RequestRouting_EachServiceOnlyRespondsToItsOwnQueue(CancellationToken cancellationToken)
    {
        // Each request goes to one specific queue; the response topic is unique per request.
        // After collecting both expected responses we verify that no cross-routing happened
        // (each topic received exactly one message).
        var alphaResponseTopic = $"system-test-routing-alpha-{Guid.NewGuid():N}";
        var betaResponseTopic = $"system-test-routing-beta-{Guid.NewGuid():N}";

        await Bus.DeclareQueueAsync(alphaResponseTopic, cancellationToken);
        await Bus.DeclareQueueAsync(betaResponseTopic, cancellationToken);

        var alphaMessages = new List<IdentificationResponseMessage>();
        var betaMessages = new List<IdentificationResponseMessage>();
        var alphaTcs = new TaskCompletionSource<bool>();
        var betaTcs = new TaskCompletionSource<bool>();

        await using var alphaSub = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            alphaResponseTopic,
            (msg, _) =>
            {
                alphaMessages.Add(msg);
                alphaTcs.TrySetResult(true);
                return Task.CompletedTask;
            },
            cancellationToken);

        await using var betaSub = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            betaResponseTopic,
            (msg, _) =>
            {
                betaMessages.Add(msg);
                betaTcs.TrySetResult(true);
                return Task.CompletedTask;
            },
            cancellationToken);

        // Send one request to each queue
        await Bus.PublishAsync(DualBackendServiceEnvironment.AlphaQueueName,
            new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), alphaResponseTopic),
            cancellationToken);

        await Bus.PublishAsync(DualBackendServiceEnvironment.BetaQueueName,
            new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), betaResponseTopic),
            cancellationToken);

        var timeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        var bothResponded = Task.WhenAll(alphaTcs.Task, betaTcs.Task);
        Assert.That(await Task.WhenAny(bothResponded, timeout), Is.EqualTo(bothResponded),
            "Did not receive responses from both services within 30 seconds.");

        // Wait a further 3 seconds to confirm no extra messages arrive
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        Assert.That(alphaMessages, Has.Count.EqualTo(1),
            "Alpha response topic should have received exactly one message. " +
            "More messages indicate cross-routing from Beta.");

        Assert.That(betaMessages, Has.Count.EqualTo(1),
            "Beta response topic should have received exactly one message. " +
            "More messages indicate cross-routing from Alpha.");

        Assert.That(alphaMessages[0].ServiceId, Is.Not.EqualTo(betaMessages[0].ServiceId),
            "Alpha and Beta should have responded with distinct ServiceIds.");
    }

    [Test]
    [CancelAfter(45_000)]
    public async Task WhenRequestSentToAlphaQueue_OnlyAlphaResponds_BetaIsQuiet(CancellationToken cancellationToken)
    {
        var alphaResponseTopic = $"system-test-alpha-only-{Guid.NewGuid():N}";
        var betaSilenceTopic = $"system-test-beta-silence-{Guid.NewGuid():N}";

        await Bus.DeclareQueueAsync(alphaResponseTopic, cancellationToken);
        await Bus.DeclareQueueAsync(betaSilenceTopic, cancellationToken);

        var alphaReceived = new TaskCompletionSource<IdentificationResponseMessage>();
        var betaReceivedAny = false;

        await using var alphaSub = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            alphaResponseTopic,
            (msg, _) => { alphaReceived.TrySetResult(msg); return Task.CompletedTask; },
            cancellationToken);

        await using var betaSub = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            betaSilenceTopic,
            (_, _) => { betaReceivedAny = true; return Task.CompletedTask; },
            cancellationToken);

        // Publish ONLY to the Alpha queue
        await Bus.PublishAsync(DualBackendServiceEnvironment.AlphaQueueName,
            new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), alphaResponseTopic),
            cancellationToken);

        // Alpha must respond within 20 seconds
        var timeout = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        Assert.That(await Task.WhenAny(alphaReceived.Task, timeout), Is.EqualTo(alphaReceived.Task),
            "Alpha did not respond within 20 seconds.");

        // Wait an additional 3 seconds to give Beta any chance to (incorrectly) react
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        Assert.That(betaReceivedAny, Is.False,
            "Beta responded to a request sent to Alpha's queue — it should not have.");
    }

    [Test]
    [CancelAfter(45_000)]
    public async Task WhenRequestSentToBetaQueue_OnlyBetaResponds_AlphaIsQuiet(CancellationToken cancellationToken)
    {
        var betaResponseTopic = $"system-test-beta-only-{Guid.NewGuid():N}";
        var alphaSilenceTopic = $"system-test-alpha-silence-{Guid.NewGuid():N}";

        await Bus.DeclareQueueAsync(betaResponseTopic, cancellationToken);
        await Bus.DeclareQueueAsync(alphaSilenceTopic, cancellationToken);

        var betaReceived = new TaskCompletionSource<IdentificationResponseMessage>();
        var alphaReceivedAny = false;

        await using var betaSub = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            betaResponseTopic,
            (msg, _) => { betaReceived.TrySetResult(msg); return Task.CompletedTask; },
            cancellationToken);

        await using var alphaSub = await Bus.SubscribeAsync<IdentificationResponseMessage>(
            alphaSilenceTopic,
            (_, _) => { alphaReceivedAny = true; return Task.CompletedTask; },
            cancellationToken);

        // Publish ONLY to the Beta queue
        await Bus.PublishAsync(DualBackendServiceEnvironment.BetaQueueName,
            new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), betaResponseTopic),
            cancellationToken);

        // Beta must respond within 20 seconds
        var timeout = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        Assert.That(await Task.WhenAny(betaReceived.Task, timeout), Is.EqualTo(betaReceived.Task),
            "Beta did not respond within 20 seconds.");

        // Wait an additional 3 seconds to give Alpha any chance to (incorrectly) react
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        Assert.That(alphaReceivedAny, Is.False,
            "Alpha responded to a request sent to Beta's queue — it should not have.");
    }
}

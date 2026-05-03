using Auxilia.Messaging;
using Auxilia.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Auxilia.BackendService.Tests.ComponentTests;

[TestFixture]
[Category("Component")]
public class BackendServiceStartupTests
{
    [SetUp]
    public async Task SetUp()
    {
        _fakeBus = new FakeMessageBusClient();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMessageBusClient>(_fakeBus);
                services.AddSingleton<ServiceInfo>();
                services.AddHostedService<QueueInitializer>();
                services.AddHostedService<IdentificationRequestHandler>();
            })
            .Build();

        await _host.StartAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private FakeMessageBusClient _fakeBus = null!;
    private IHost _host = null!;

    [Test]
    public async Task WhenServiceStarts_BackendServiceQueueIsDeclaredWithinTenSeconds()
    {
        var declared = await _fakeBus.WaitForConditionAsync(
            () => _fakeBus.DeclaredQueues.Contains(QueueInitializer.DefaultQueueName),
            TimeSpan.FromSeconds(10));

        Assert.That(declared, Is.True, $"Queue '{QueueInitializer.DefaultQueueName}' was not declared within 10 seconds.");
    }

    [Test]
    public async Task WhenIdentificationRequestReceived_ValidResponseIsPublishedWithinTenSeconds()
    {
        // Wait for startup to complete
        await _fakeBus.WaitForConditionAsync(
            () => _fakeBus.DeclaredQueues.Contains(QueueInitializer.DefaultQueueName),
            TimeSpan.FromSeconds(10));

        const string responseTopic = "test-response-topic";
        var request = new IdentificationRequestMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            responseTopic);

        await _fakeBus.SimulateReceivedAsync(QueueInitializer.DefaultQueueName, request);

        var responded = await _fakeBus.WaitForConditionAsync(
            () => _fakeBus.PublishedMessages.Any(m => m.Topic == responseTopic),
            TimeSpan.FromSeconds(10));

        Assert.That(responded, Is.True, "No identification response was published within 10 seconds.");

        var (_, msg) = _fakeBus.PublishedMessages.First(m => m.Topic == responseTopic);
        var response = msg as IdentificationResponseMessage;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.ServicePurpose, Is.EqualTo("AuxiliaBackendService"));
        Assert.That(response.ServiceId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(response.Version, Is.Not.Null.And.Not.Empty);
        Assert.That(response.StartupTimeUtc, Is.LessThanOrEqualTo(DateTime.UtcNow));
    }
}